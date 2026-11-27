using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace TruckingAdvisor.Core.Speech;

/// <summary>
/// Downloads a model file once into the app's data folder. Interrupted downloads resume; a pinned
/// SHA-256 is checked before the file is used (a mismatch deletes it). Only HTTPS from huggingface.co.
/// A connection that stops sending data for <see cref="IdleTimeout"/> is dropped (the partial file is kept
/// and resumes next time), so a stalled download never hangs forever.
/// </summary>
public sealed class ModelDownloader : IDisposable
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    public string Folder { get; }

    /// <summary>How long the server may go without sending anything (headers or data) before the download is dropped.</summary>
    public TimeSpan IdleTimeout { get; }

    public ModelDownloader(string folder, HttpMessageHandler? handler = null, TimeSpan? idleTimeout = null)
    {
        Folder = folder;
        IdleTimeout = idleTimeout ?? DefaultIdleTimeout;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        // Big files on slow lines can take a long time in total; stalls are caught by IdleTimeout instead.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TruckingAdvisor", "3"));
    }

    public string PathFor(WhisperModelInfo model) => Path.Combine(Folder, model.FileName);

    /// <summary>True when the model is on disk (and matches its checksum if one is pinned and verified before).</summary>
    public bool IsReady(WhisperModelInfo model) => File.Exists(PathFor(model)) && new FileInfo(PathFor(model)).Length > 1_000_000;

    public Task<string> EnsureAsync(WhisperModelInfo model, IProgress<double>? progress = null, CancellationToken ct = default) =>
        EnsureFileAsync(model.Url, model.FileName, model.Sha256, model.ApproxBytes, progress, ct);

    /// <summary>Downloads one pinned file into the folder (resuming a partial one) and checks its SHA-256.</summary>
    public async Task<string> EnsureFileAsync(string url, string fileName, string sha256, long approxBytes,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string target = Path.Combine(Folder, fileName);
        if (File.Exists(target) && new FileInfo(target).Length == approxBytes) return target;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "huggingface.co")
            throw new InvalidOperationException("Untrusted model address.");
        Directory.CreateDirectory(Folder);
        string partial = target + ".partial";

        // The app may have closed between the last byte and the rename: a partial that is already full size is
        // either the finished file (use it) or junk (start over). Asking the server for the bytes after its end
        // would only get "416 Range Not Satisfiable" on every try.
        if (approxBytes > 0 && File.Exists(partial) && new FileInfo(partial).Length >= approxBytes
            && await FinishPartialAsync(partial, target, sha256, ct).ConfigureAwait(false))
        {
            progress?.Report(1);
            return target;
        }

        for (int attempt = 0; ; attempt++)
        {
            long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);

            idle.CancelAfter(IdleTimeout);
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false); }
            catch (Exception ex) when (IsStall(ex, idle, ct)) { throw Stalled(fileName); }

            using (resp)
            {
                if (have > 0 && resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // The partial file already holds every byte (or more than the file now has).
                    if (await FinishPartialAsync(partial, target, sha256, ct).ConfigureAwait(false))
                    {
                        progress?.Report(1);
                        return target;
                    }
                    if (attempt == 0) continue; // the partial was deleted: download once more from the start
                    throw new InvalidDataException($"The server refused to resume {fileName}. Try again later.");
                }
                if (have > 0 && resp.StatusCode != HttpStatusCode.PartialContent) have = 0; // server restarted it
                resp.EnsureSuccessStatusCode();
                if (resp.RequestMessage?.RequestUri is { Scheme: not "https" }) throw new InvalidDataException("Download left HTTPS.");
                long total = (resp.Content.Headers.ContentLength ?? approxBytes - have) + have;

                try
                {
                    await using var input = await resp.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                    await using var output = new FileStream(partial, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write);
                    var buffer = new byte[1 << 16];
                    long done = have;
                    while (true)
                    {
                        idle.CancelAfter(IdleTimeout); // restarts the clock: only a silent connection is dropped
                        int n = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                        if (n == 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        done += n;
                        if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
                    }
                }
                catch (Exception ex) when (IsStall(ex, idle, ct)) { throw Stalled(fileName); }
            }

            string sha = await HashAsync(partial, ct).ConfigureAwait(false);
            if (sha256.Length > 0 && !string.Equals(sha, sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException($"The downloaded {fileName} doesn't match its checksum, so it was deleted. Try again later.");
            }
            File.Move(partial, target, overwrite: true);
            return target;
        }
    }

    /// <summary>Moves a complete partial file into place if it matches the pinned checksum; otherwise deletes it.</summary>
    private static async Task<bool> FinishPartialAsync(string partial, string target, string sha256, CancellationToken ct)
    {
        if (sha256.Length > 0 && string.Equals(await HashAsync(partial, ct).ConfigureAwait(false), sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(partial, target, overwrite: true);
            return true;
        }
        File.Delete(partial);
        return false;
    }

    /// <summary>True when the idle timer (not the caller) cancelled the request or the read.</summary>
    private static bool IsStall(Exception ex, CancellationTokenSource idle, CancellationToken ct) =>
        ex is (OperationCanceledException or IOException) && idle.IsCancellationRequested && !ct.IsCancellationRequested;

    private TimeoutException Stalled(string fileName) =>
        new($"The download of {fileName} stalled (nothing received for {IdleTimeout.TotalSeconds:0} s). It resumes from where it stopped next time.");

    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    public void Dispose() => _http.Dispose();
}
