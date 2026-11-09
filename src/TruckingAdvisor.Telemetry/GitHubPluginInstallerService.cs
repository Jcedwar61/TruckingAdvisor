using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TruckingAdvisor.Telemetry;

/// <summary>
/// Pinned plugin release. TruckingAdvisor's offsets are built for this exact layout revision,
/// so we never install "latest".
/// </summary>
public sealed record PinnedPluginRelease(
    string OwnerRepo,
    string Tag,
    string TargetDllName,
    /// <summary>
    /// Expected SHA-256 of the downloaded release asset (lowercase hex). When empty, the installer
    /// computes the hash and asks the user to confirm it before installing anything.
    /// </summary>
    string ExpectedAssetSha256)
{
    public string ReleaseApiUrl => $"https://api.github.com/repos/{OwnerRepo}/releases/tags/{Tag}";
    public string ReleasePageUrl => $"https://github.com/{OwnerRepo}/releases/tag/{Tag}";
}

public sealed record DownloadedPlugin(string StagedDllPath, string AssetName, string AssetSha256, bool HashVerified);

/// <summary>
/// Finds ATS / ETS2 installs and installs the RenCloud scs-sdk-plugin.
///
/// Flow (the UI drives it so the user sees and approves each step):
/// 1. <see cref="DownloadAsync"/> - downloads the pinned release asset and stages the x64 DLL.
/// 2. The UI shows <see cref="DownloadedPlugin.AssetSha256"/> and asks the user to confirm
///    (unless it matched <see cref="PinnedPluginRelease.ExpectedAssetSha256"/>).
/// 3. <see cref="InstallToGames"/> - copies the DLL into each game's bin\win_x64\plugins folder,
///    backing up any existing DLL first. Steam folders under Program Files may need admin rights;
///    those failures are reported, not hidden.
/// </summary>
public sealed class GitHubPluginInstallerService
{
    public static readonly PinnedPluginRelease RenCloudPlugin = new(
        OwnerRepo: "RenCloud/scs-sdk-plugin",
        Tag: "V.1.12.1",
        TargetDllName: "scs-telemetry.dll",
        ExpectedAssetSha256: "");

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TruckingAdvisor", "2.2"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private readonly GameLocator _locator;

    public GitHubPluginInstallerService(GameLocator? locator = null) => _locator = locator ?? new GameLocator();

    /// <summary>Verified ATS / ETS2 installs on any drive, plus folders the user picked. Creates nothing.</summary>
    public IReadOnlyList<GameInstall> FindGames(IEnumerable<string>? extraFolders = null) => _locator.FindAll(extraFolders);

    /// <summary>Checks a folder the user picked; null if it isn't ATS/ETS2.</summary>
    public GameInstall? VerifyGameFolder(string folder) => _locator.Verify(folder);

    public async Task<DownloadedPlugin> DownloadAsync(string stagingDir, CancellationToken ct = default)
    {
        var release = RenCloudPlugin;
        Directory.CreateDirectory(stagingDir);

        using var resp = await Http.GetAsync(release.ReleaseApiUrl, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
            .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || a.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (string.IsNullOrEmpty(asset.Url))
            throw new InvalidOperationException($"No .zip or .dll asset found in {release.ReleasePageUrl}");

        byte[] bytes = await Http.GetByteArrayAsync(asset.Url, ct);
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        bool verified = !string.IsNullOrEmpty(release.ExpectedAssetSha256);
        if (verified && !string.Equals(sha, release.ExpectedAssetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Checksum mismatch for {asset.Name}: got {sha}, expected {release.ExpectedAssetSha256}. Nothing was installed.");

        string staged = Path.Combine(stagingDir, release.TargetDllName);
        if (asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            await File.WriteAllBytesAsync(staged, bytes, ct);
        }
        else
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entry = PickX64Dll(zip.Entries.Select(e => e.FullName).ToList(), release.TargetDllName);
            if (entry is null)
                throw new InvalidOperationException($"No 64-bit {release.TargetDllName} found inside {asset.Name}.");
            // Extract to our fixed staging path only (never to a path taken from the archive).
            zip.GetEntry(entry)!.ExtractToFile(staged, overwrite: true);
        }

        return new DownloadedPlugin(staged, asset.Name, sha, verified);
    }

    /// <summary>Chooses the 64-bit DLL from archive entry names. ATS/ETS2 are 64-bit only.</summary>
    public static string? PickX64Dll(IReadOnlyList<string> entryNames, string dllName)
    {
        var dlls = entryNames.Where(n => n.EndsWith(dllName, StringComparison.OrdinalIgnoreCase)).ToList();
        return dlls.FirstOrDefault(n => Regex.IsMatch(n, @"(x64|win64|win_x64)", RegexOptions.IgnoreCase))
               ?? (dlls.Count == 1 && !Regex.IsMatch(dlls[0], @"(x86|win32)", RegexOptions.IgnoreCase) ? dlls[0] : null);
    }

    /// <summary>Copies the staged DLL into each game. Returns one line per game, including failures.</summary>
    public IReadOnlyList<string> InstallToGames(string stagedDll, IEnumerable<GameInstall> games)
    {
        var log = new List<string>();
        foreach (var game in games)
        {
            var pluginsDir = game.PluginFolder;
            var dest = Path.Combine(pluginsDir, RenCloudPlugin.TargetDllName);
            try
            {
                Directory.CreateDirectory(pluginsDir);
                if (File.Exists(dest))
                {
                    var backup = dest + $".bak-{DateTime.Now:yyyyMMddHHmmss}";
                    File.Copy(dest, backup);
                    log.Add($"Backed up existing plugin -> {backup}");
                }
                File.Copy(stagedDll, dest, overwrite: true);
                log.Add($"Installed for {game.Name} -> {dest}");
            }
            catch (UnauthorizedAccessException)
            {
                log.Add($"Permission denied for {pluginsDir}. Run TruckingAdvisor as administrator once, or copy {stagedDll} there by hand.");
            }
            catch (IOException ex)
            {
                log.Add($"Could not write {dest}: {ex.Message} (close the game and try again)");
            }
        }
        if (log.Count == 0) log.Add("No ATS or ETS2 install selected. Pick the game folder, or copy the DLL into <game>\\bin\\win_x64\\plugins by hand.");
        return log;
    }
}
