using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace TruckingAdvisor.Core.Speech;

/// <summary>
/// Downloads a speech model once into the app's data folder. Interrupted downloads resume; a pinned
/// SHA-256 is checked before the file is used (a mismatch deletes it). Only HTTPS from huggingface.co.
/// </summary>
public sealed class ModelDownloader
{
    private readonly HttpClient _http;
    public string Folder { get; }

    public ModelDownloader(string folder, HttpMessageHandler? handler = null)
    {
        Folder = folder;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TruckingAdvisor", "3"));
    }

    public string PathFor(WhisperModelInfo model) => Path.Combine(Folder, model.FileName);

    /// <summary>True when the model is on disk (and matches its checksum if one is pinned and verified before).</summary>
    public bool IsReady(WhisperModelInfo model) => File.Exists(PathFor(model)) && new FileInfo(PathFor(model)).Length > 1_000_000;

    public async Task<string> EnsureAsync(WhisperModelInfo model, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string target = PathFor(model);
        if (IsReady(model)) return target;
        if (!Uri.TryCreate(model.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "huggingface.co")
            throw new InvalidOperationException("Untrusted model address.");
        Directory.CreateDirectory(Folder);
        string partial = target + ".partial";
        long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (have > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // server restarted it
        resp.EnsureSuccessStatusCode();
        if (resp.RequestMessage?.RequestUri is { Scheme: not "https" }) throw new InvalidDataException("Download left HTTPS.");
        long total = (resp.Content.Headers.ContentLength ?? model.ApproxBytes - have) + have;

        await using (var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(partial, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write))
        {
            var buffer = new byte[1 << 16];
            long done = have;
            int n;
            while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
            }
        }

        string sha = await HashAsync(partial, ct).ConfigureAwait(false);
        if (model.Sha256.Length > 0 && !string.Equals(sha, model.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new InvalidDataException($"The downloaded {model.Id} model doesn't match its checksum, so it was deleted. Try again later.");
        }
        File.Move(partial, target, overwrite: true);
        return target;
    }

    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
    }
}
