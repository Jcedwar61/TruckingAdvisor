using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TruckingAdvisor.Core.Updates;

/// <summary>A newer release that has a verifiable Windows installer.</summary>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string InstallerName,
    string InstallerUrl,
    long InstallerSize,
    string ChecksumsUrl,
    string ReleasePageUrl,
    string Notes);

/// <summary>
/// Finds new versions on the project's GitHub Releases page and downloads the installer.
/// Only installers that match the release's SHA256SUMS.txt are ever returned, and only over HTTPS
/// from github.com. Sends no personal data (a plain "TruckingAdvisor/x.y.z" user agent).
/// </summary>
public sealed class UpdateChecker
{
    public const string DefaultRepository = "Jcedwar61/TruckingAdvisor";
    private static readonly Regex InstallerName = new(@"^TruckingAdvisor-Setup-(\d+\.\d+\.\d+)\.exe$", RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly string _repository;

    public Version CurrentVersion { get; }

    public UpdateChecker(Version currentVersion, HttpMessageHandler? handler = null, string repository = DefaultRepository)
    {
        CurrentVersion = Normalize(currentVersion);
        _repository = repository;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TruckingAdvisor", CurrentVersion.ToString(3)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>The latest published (non-draft, non-prerelease) version if it's newer than this one; otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"https://api.github.com/repos/{_repository}/releases/latest", ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return Parse(doc.RootElement, CurrentVersion, _repository);
    }

    /// <summary>Reads a GitHub "release" object. Public for tests.</summary>
    public static UpdateInfo? Parse(JsonElement release, Version current, string repository = DefaultRepository)
    {
        if (Bool(release, "draft") || Bool(release, "prerelease")) return null;
        string tag = Str(release, "tag_name");
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = Normalize(version);
        if (version <= Normalize(current)) return null;

        string? installerName = null, installerUrl = null, sumsUrl = null;
        long size = 0;
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = Str(a, "name"), url = Str(a, "browser_download_url");
                if (!IsTrustedUrl(url, repository)) continue;
                var m = InstallerName.Match(name);
                if (m.Success && Version.TryParse(m.Groups[1].Value, out var assetVersion) && Normalize(assetVersion) == version)
                {
                    installerName = name;
                    installerUrl = url;
                    size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
                }
                else if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    sumsUrl = url;
                }
            }
        }
        // No checksum file, no update: we never install something we can't verify.
        if (installerName is null || installerUrl is null || sumsUrl is null) return null;

        string page = Str(release, "html_url");
        if (!page.StartsWith($"https://github.com/{repository}/", StringComparison.OrdinalIgnoreCase))
            page = $"https://github.com/{repository}/releases/tag/{tag}";
        return new UpdateInfo(version, tag, installerName, installerUrl, size, sumsUrl, page, Str(release, "body").Trim());
    }

    /// <summary>
    /// Downloads the installer into <paramref name="folder"/> and checks its SHA-256 against SHA256SUMS.txt.
    /// Reuses an earlier verified download. Throws if the file doesn't match.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo update, string folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        string sums = await _http.GetStringAsync(update.ChecksumsUrl, ct).ConfigureAwait(false);
        string expected = ParseChecksums(sums).TryGetValue(update.InstallerName, out var h)
            ? h
            : throw new InvalidDataException($"SHA256SUMS.txt has no entry for {update.InstallerName}.");

        string target = Path.Combine(folder, update.InstallerName);
        if (File.Exists(target) && string.Equals(await HashFileAsync(target, ct).ConfigureAwait(false), expected, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(1);
            return target;
        }

        string partial = target + ".partial";
        using (var response = await _http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { Scheme: not "https" })
                throw new InvalidDataException("The download was redirected away from HTTPS.");
            long total = response.Content.Headers.ContentLength ?? update.InstallerSize;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(partial);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
            }
            string actual = Convert.ToHexString(sha.GetHashAndReset());
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                output.Close();
                File.Delete(partial);
                throw new InvalidDataException("The downloaded installer doesn't match its published checksum, so it was deleted.");
            }
        }
        File.Move(partial, target, overwrite: true);
        // Old installers aren't needed any more.
        foreach (var old in Directory.GetFiles(folder, "TruckingAdvisor-Setup-*.exe*"))
            if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase)) TryDelete(old);
        return target;
    }

    /// <summary>"&lt;sha256&gt;  &lt;file&gt;" lines (sha256sum format; '*' binary marker and a BOM are allowed).</summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("﻿", "").Split('\n'))
        {
            var m = Regex.Match(raw.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
            if (m.Success) result[Path.GetFileName(m.Groups[2].Value.Trim())] = m.Groups[1].Value.ToLowerInvariant();
        }
        return result;
    }

    /// <summary>Only this project's release downloads on github.com (GitHub then redirects to its own CDN over HTTPS).</summary>
    public static bool IsTrustedUrl(string url, string repository = DefaultRepository) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith($"/{repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Release notes links open only if they point at this project's GitHub page.</summary>
    public static bool IsTrustedReleasePage(string url, string repository = DefaultRepository) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith($"/{repository}/releases/", StringComparison.OrdinalIgnoreCase);

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
