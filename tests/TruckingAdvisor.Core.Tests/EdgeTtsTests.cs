using TruckingAdvisor.EdgeTts;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

public class EdgeTtsTests
{
    // Reference values from the Python edge-tts 7.3.1 package: edge_tts.drm.generate_sec_ms_gec_v2(t).
    [Theory]
    [InlineData(1700000000.0, "2CD23A2CA0B2595A-F3C846C370E05F9CC83ACC5F995C5F5D87A4D0F14CE8936A0F902E64099E38A2")]
    [InlineData(1763112345.678, "0CDEE254AFE46BCE-49E5EB28DA10C4E6326A956BDC2D859E8AA29E427F4AC2CAA4A2C78A27B5C9C8")]
    [InlineData(1800000123.25, "99E54519B490BE5C-E162DB0977FBDCB9B86DD8148BF1BCDC3C0303176CE39852A42B415935C20654")]
    [InlineData(2000000000.0, "2D62557F1142BC5D-FD9C30C68FF194A5EE6B45FEFC5E2BA1D1239F88A90569AB55CFFFAA7799F40E")]
    public void Token_MatchesThePythonReference(double unixTime, string expected) =>
        Assert.Equal(expected, EdgeTtsToken.Generate(unixTime));
}
