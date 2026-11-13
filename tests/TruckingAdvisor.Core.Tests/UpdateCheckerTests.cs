using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TruckingAdvisor.Core.Updates;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

public class UpdateCheckerTests
{
    private const string Base = "https://github.com/Jcedwar61/TruckingAdvisor/releases/download/v2.9.0/";
    private static readonly byte[] Installer = Encoding.ASCII.GetBytes("pretend installer bytes");
    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static string Release(string tag = "v2.9.0", bool sums = true, bool prerelease = false, string host = "github.com") => $$"""
        {"tag_name":"{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}},
         "html_url":"https://github.com/Jcedwar61/TruckingAdvisor/releases/tag/{{tag}}","body":"Notes here",
         "assets":[
           {"name":"TruckingAdvisor-Setup-{{tag.TrimStart('v')}}.exe","size":{{Installer.Length}},
            "browser_download_url":"https://{{host}}/Jcedwar61/TruckingAdvisor/releases/download/{{tag}}/TruckingAdvisor-Setup-{{tag.TrimStart('v')}}.exe"}
           {{(sums ? $",{{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://{host}/Jcedwar61/TruckingAdvisor/releases/download/{tag}/SHA256SUMS.txt\"}}" : "")}}
         ]}
        """;

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public string ReleaseJson = Release();
        public byte[] InstallerBytes = Installer;
        public string Sums = $"{Hash(Installer)}  TruckingAdvisor-Setup-2.9.0.exe\n{new string('a', 64)}  TruckingAdvisor.exe\n";
        public List<string> Requests = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();
            Requests.Add(url);
            HttpContent content = url.EndsWith("/releases/latest") ? new StringContent(ReleaseJson)
                : url.EndsWith("SHA256SUMS.txt") ? new StringContent(Sums)
                : url.EndsWith(".exe") ? new ByteArrayContent(InstallerBytes)
                : new StringContent("");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    private static UpdateInfo? ParseRelease(string json, string current) =>
        UpdateChecker.Parse(JsonDocument.Parse(json).RootElement, Version.Parse(current));

    [Fact]
    public void NewerRelease_IsOffered()
    {
        var u = ParseRelease(Release(), "2.8.1");
        Assert.NotNull(u);
        Assert.Equal(new Version(2, 9, 0), u!.Version);
        Assert.Equal("TruckingAdvisor-Setup-2.9.0.exe", u.InstallerName);
        Assert.Equal("Notes here", u.Notes);
    }

    [Theory]
    [InlineData("2.9.0")]
    [InlineData("2.9.0.0")] // assembly versions have four parts
    [InlineData("3.0.0")]
    public void SameOrOlderRelease_IsIgnored(string current) => Assert.Null(ParseRelease(Release(), current));

    [Fact]
    public void Prerelease_IsIgnored() => Assert.Null(ParseRelease(Release(prerelease: true), "2.8.1"));

    [Fact]
    public void ReleaseWithoutChecksums_IsNeverOffered() => Assert.Null(ParseRelease(Release(sums: false), "2.8.1"));

    [Fact]
    public void AssetsFromOtherHosts_AreRejected()
    {
        Assert.Null(ParseRelease(Release(host: "evil.example.com"), "2.8.1"));
        Assert.False(UpdateChecker.IsTrustedUrl("http://github.com/Jcedwar61/TruckingAdvisor/releases/download/v1/x.exe"));
        Assert.False(UpdateChecker.IsTrustedUrl("https://github.com/someone/else/releases/download/v1/x.exe"));
        Assert.True(UpdateChecker.IsTrustedUrl(Base + "TruckingAdvisor-Setup-2.9.0.exe"));
    }

    [Fact]
    public void ReleaseNotesLink_OnlyOpensThisProjectsPage()
    {
        Assert.True(UpdateChecker.IsTrustedReleasePage("https://github.com/Jcedwar61/TruckingAdvisor/releases/tag/v2.9.0"));
        Assert.False(UpdateChecker.IsTrustedReleasePage("https://example.com/Jcedwar61/TruckingAdvisor/releases/tag/v2.9.0"));
        Assert.False(UpdateChecker.IsTrustedReleasePage("file:///C:/Windows/System32/calc.exe"));
    }

    [Fact]
    public void Checksums_ParseShaSumFormat()
    {
        var sums = UpdateChecker.ParseChecksums($"﻿{new string('A', 64)}  TruckingAdvisor-Setup-2.9.0.exe\r\n{new string('b', 64)} *other.exe\njunk line");
        Assert.Equal(new string('a', 64), sums["TruckingAdvisor-Setup-2.9.0.exe"]);
        Assert.Equal(new string('b', 64), sums["other.exe"]);
        Assert.Equal(2, sums.Count);
    }

    [Fact]
    public async Task Download_VerifiesChecksum_AndReusesAVerifiedFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ta-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            var gh = new FakeGitHub();
            var checker = new UpdateChecker(new Version(2, 8, 1), gh);
            var update = await checker.CheckAsync();
            Assert.NotNull(update);

            string path = await checker.DownloadAsync(update!, dir);
            Assert.Equal(Installer, File.ReadAllBytes(path));
            int downloads = gh.Requests.Count(r => r.EndsWith(".exe"));

            await checker.DownloadAsync(update!, dir); // already there and verified
            Assert.Equal(downloads, gh.Requests.Count(r => r.EndsWith(".exe")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Download_RejectsTamperedInstaller()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ta-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            var gh = new FakeGitHub { InstallerBytes = Encoding.ASCII.GetBytes("something else") };
            var checker = new UpdateChecker(new Version(2, 8, 1), gh);
            var update = await checker.CheckAsync();
            await Assert.ThrowsAsync<InvalidDataException>(() => checker.DownloadAsync(update!, dir));
            Assert.Empty(Directory.GetFiles(dir)); // nothing left behind
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Check_SendsNoPersonalData()
    {
        var gh = new FakeGitHub();
        await new UpdateChecker(new Version(2, 8, 1), gh).CheckAsync();
        Assert.Equal("https://api.github.com/repos/Jcedwar61/TruckingAdvisor/releases/latest", gh.Requests.Single());
    }
}
