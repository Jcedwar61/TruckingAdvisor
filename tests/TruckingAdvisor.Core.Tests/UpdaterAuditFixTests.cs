using System.Net;
using System.Security.Cryptography;
using System.Text;
using TruckingAdvisor.Core.Speech;
using TruckingAdvisor.Core.Updates;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

/// <summary>Model downloads that can't get stuck, and update installers re-checked right before they run.</summary>
public class UpdaterAuditFixTests : IDisposable
{
    private const string ModelUrl = "https://huggingface.co/someone/model/resolve/main/model.bin";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ta-audit-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Model = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7 % 253)).ToArray();
    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    public UpdaterAuditFixTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>Serves <see cref="Model"/> with Range support; a range at or past the end gets 416 like huggingface.co.</summary>
    private sealed class FakeHub : HttpMessageHandler
    {
        public List<long> Requests = new();
        public Func<CancellationToken, Task>? BeforeHeaders;
        public Func<long, Stream>? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            long from = r.Headers.Range?.Ranges.First().From ?? 0;
            Requests.Add(from);
            if (BeforeHeaders is not null) await BeforeHeaders(ct);
            if (from >= Model.Length)
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { RequestMessage = r };
            HttpContent content = Body is null ? new ByteArrayContent(Model[(int)from..]) : new StreamContent(Body(from));
            return new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content, RequestMessage = r };
        }
    }

    /// <summary>Sends some bytes, then goes silent until cancelled (a stalled connection).</summary>
    private sealed class StallingStream : Stream
    {
        private readonly byte[] _first;
        private bool _sent;
        public StallingStream(byte[] first) => _first = first;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_sent)
            {
                _sent = true;
                _first.CopyTo(buffer);
                return _first.Length;
            }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private string Partial => Path.Combine(_dir, "model.bin.partial");
    private string Target => Path.Combine(_dir, "model.bin");

    [Fact]
    public async Task CompletePartial_LeftBeforeTheRename_IsUsedWithoutDownloading()
    {
        var hub = new FakeHub();
        File.WriteAllBytes(Partial, Model);
        using var dl = new ModelDownloader(_dir, hub);
        string path = await dl.EnsureFileAsync(ModelUrl, "model.bin", Hash(Model), Model.Length);
        Assert.Equal(Model, File.ReadAllBytes(path));
        Assert.False(File.Exists(Partial));
        Assert.Empty(hub.Requests);
    }

    [Fact]
    public async Task FullSizePartial_ThatDoesNotMatch_IsDeletedAndDownloadedAgain()
    {
        var hub = new FakeHub();
        File.WriteAllBytes(Partial, new byte[Model.Length]);
        using var dl = new ModelDownloader(_dir, hub);
        string path = await dl.EnsureFileAsync(ModelUrl, "model.bin", Hash(Model), Model.Length);
        Assert.Equal(Model, File.ReadAllBytes(path));
        Assert.Equal(new long[] { 0 }, hub.Requests);
    }

    [Fact]
    public async Task RangeNotSatisfiable_OnACompletePartial_FinishesIt()
    {
        // The pinned size is only approximate (as for the Whisper models), so only the server's 416 says it's complete.
        var hub = new FakeHub();
        File.WriteAllBytes(Partial, Model);
        using var dl = new ModelDownloader(_dir, hub);
        string path = await dl.EnsureFileAsync(ModelUrl, "model.bin", Hash(Model), Model.Length + 50_000);
        Assert.Equal(Model, File.ReadAllBytes(path));
        Assert.Equal(new long[] { Model.Length }, hub.Requests);
    }

    [Fact]
    public async Task RangeNotSatisfiable_OnAJunkPartial_RestartsOnceFromTheStart()
    {
        var hub = new FakeHub();
        File.WriteAllBytes(Partial, new byte[Model.Length + 10]);
        using var dl = new ModelDownloader(_dir, hub);
        string path = await dl.EnsureFileAsync(ModelUrl, "model.bin", Hash(Model), Model.Length + 50_000);
        Assert.Equal(Model, File.ReadAllBytes(path));
        Assert.Equal(new long[] { Model.Length + 10, 0 }, hub.Requests);
    }

    [Fact]
    public async Task StalledDownload_TimesOut_AndKeepsWhatArrivedForNextTime()
    {
        var hub = new FakeHub { Body = from => new StallingStream(Model[(int)from..((int)from + 1000)]) };
        using var dl = new ModelDownloader(_dir, hub, idleTimeout: TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => dl.EnsureFileAsync(ModelUrl, "model.bin", Hash(Model), Model.Length));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
        Assert.Equal(1000, new FileInfo(Partial).Length);
        Assert.False(File.Exists(Target));

        // The next try resumes from there and finishes.
        hub.Body = null;
        string path = await dl.EnsureFileAsync(ModelUrl, "model.bin", Hash(Model), Model.Length);
        Assert.Equal(Model, File.ReadAllBytes(path));
        Assert.Equal(1000, hub.Requests[^1]);
    }

    [Fact]
    public async Task ServerThatNeverAnswers_TimesOut()
    {
        var hub = new FakeHub { BeforeHeaders = ct => Task.Delay(Timeout.Infinite, ct) };
        using var dl = new ModelDownloader(_dir, hub, idleTimeout: TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<TimeoutException>(() => dl.EnsureFileAsync(ModelUrl, "model.bin", Hash(Model), Model.Length));
    }

    [Fact]
    public async Task CallerCancellation_IsNotReportedAsAStall()
    {
        var hub = new FakeHub { BeforeHeaders = ct => Task.Delay(Timeout.Infinite, ct) };
        using var dl = new ModelDownloader(_dir, hub, idleTimeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => dl.EnsureFileAsync(ModelUrl, "model.bin", Hash(Model), Model.Length, ct: cts.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    // ---- Update installer: re-verified right before it is started ----

    private static readonly byte[] Installer = Encoding.ASCII.GetBytes("pretend installer bytes");

    [Fact]
    public void VerifiedInstaller_IsHeldOpen_SoItCantBeSwapped()
    {
        string path = Path.Combine(_dir, "TruckingAdvisor-Setup-9.9.9.exe");
        File.WriteAllBytes(path, Installer);
        using (var held = UpdateChecker.OpenVerifiedInstaller(path, Hash(Installer).ToUpperInvariant()))
        {
            Assert.NotNull(held);
            if (OperatingSystem.IsWindows())
            {
                Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(path, new byte[] { 1, 2, 3 }));
                Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".moved"));
            }
            // Others may still read it (Windows does, to start it).
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        Assert.Equal(Installer, File.ReadAllBytes(path));
    }

    [Fact]
    public void ChangedInstaller_IsRefusedAndDeleted()
    {
        string path = Path.Combine(_dir, "TruckingAdvisor-Setup-9.9.9.exe");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("swapped after the download was checked"));
        Assert.Null(UpdateChecker.OpenVerifiedInstaller(path, Hash(Installer)));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void MissingInstaller_IsRefused() =>
        Assert.Null(UpdateChecker.OpenVerifiedInstaller(Path.Combine(_dir, "nope", "TruckingAdvisor-Setup-9.9.9.exe"), Hash(Installer)));

    private sealed class FakeGitHub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();
            HttpContent content = url.EndsWith("SHA256SUMS.txt")
                ? new StringContent($"{Hash(Installer)}  TruckingAdvisor-Setup-9.9.9.exe\n")
                : new ByteArrayContent(Installer);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    [Fact]
    public async Task Download_ReturnsThePublishedChecksum_ForTheCheckBeforeLaunch()
    {
        const string Base = "https://github.com/Jcedwar61/TruckingAdvisor/releases/download/v9.9.9/";
        var update = new UpdateInfo(new Version(9, 9, 9), "v9.9.9", "TruckingAdvisor-Setup-9.9.9.exe", Base + "TruckingAdvisor-Setup-9.9.9.exe",
            Installer.Length, Base + "SHA256SUMS.txt", "https://github.com/Jcedwar61/TruckingAdvisor/releases/tag/v9.9.9", "");
        var checker = new UpdateChecker(new Version(1, 0, 0), new FakeGitHub());
        var download = await checker.DownloadVerifiedAsync(update, _dir);
        Assert.Equal(Hash(Installer), download.Sha256);
        using var held = UpdateChecker.OpenVerifiedInstaller(download.Path, download.Sha256);
        Assert.NotNull(held);
    }
}
