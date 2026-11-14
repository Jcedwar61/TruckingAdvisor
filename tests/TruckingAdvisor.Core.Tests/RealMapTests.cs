using System.Numerics;
using TruckingAdvisor.Core.Mapping;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

public class RealMapTests
{
    // A made-up "game world": real km scaled ~1:19 (52 units per km), rotated 2°, shifted. Like ATS, roughly.
    private static readonly Complex A = Complex.FromPolarCoordinates(52, 2 * Math.PI / 180);
    private static readonly Complex B = new(-40_000, 12_000);

    private static (double X, double Z) GameAt(Gazetteer gaz, double lat, double lon, double noise = 0)
    {
        var km = gaz.Project(lat, lon);
        var g = A * new Complex(km.X, km.Y) + B;
        return (g.Real + noise, -(g.Imaginary - noise));
    }

    private static LocationMemory Learned(Gazetteer gaz, params string[] names)
    {
        var mem = new LocationMemory();
        int i = 0;
        foreach (var n in names)
        {
            var t = gaz.FindTowns(n).First(x => x.Country.Contains("United States") || gaz.Region == "ets2");
            var (x, z) = GameAt(gaz, t.Lat, t.Lon, noise: (i++ % 2 == 0 ? 1 : -1) * 600); // depots aren't at the town centre
            mem.Learn(n, x, z);
        }
        return mem;
    }

    [Fact]
    public void Gazetteer_HasTownsAreasAndHighways_ForBothGames()
    {
        var ats = Gazetteer.For("ats");
        Assert.True(ats.Towns.Count > 1000);
        Assert.Contains(ats.Areas, a => a.Name == "Arizona" && a.Abbr == "AZ");
        Assert.Contains(ats.Roads, r => r.Name == "I-40");
        var ets = Gazetteer.For("ets2");
        Assert.NotEmpty(ets.FindTowns("Gothenburg"));   // matched through alternate names
        Assert.NotEmpty(ets.FindTowns("Nuremberg"));
        Assert.Contains(ets.Roads, r => r.Name.StartsWith("E"));
    }

    [Fact]
    public void Calibration_NeedsTwoCities_ThenRecoversTheMapScale()
    {
        var gaz = Gazetteer.For("ats");
        Assert.False(GeoCalibration.Fit(gaz, Learned(gaz, "Flagstaff").Cities.Values, CityStates.StateFor).IsReady);

        var cal = GeoCalibration.Fit(gaz, Learned(gaz, "Flagstaff", "Kingman", "Barstow", "Albuquerque", "Phoenix").Cities.Values, CityStates.StateFor);
        Assert.True(cal.IsReady);
        Assert.Equal("good", cal.Quality);
        Assert.InRange(cal.Scale, 50, 54);
    }

    [Fact]
    public void Describe_OnI40EastOfFlagstaff_HeadingEast()
    {
        var gaz = Gazetteer.For("ats");
        var mem = Learned(gaz, "Flagstaff", "Kingman", "Barstow", "Albuquerque", "Phoenix", "Gallup");
        var cal = GeoCalibration.Fit(gaz, mem.Cities.Values, CityStates.StateFor);
        // Real point on I-40 roughly 25 km east of Flagstaff.
        var (x, z) = GameAt(gaz, 35.205, -111.37);
        var d = PlaceDescriber.Describe(gaz, cal, x, z, headingDeg: 95, moving: true, gameCityNames: mem.Cities.Keys);

        Assert.NotNull(d);
        Assert.Equal("Arizona", d!.Area);
        Assert.Equal("I-40", d.Highway);
        Assert.Equal("Flagstaff", d.NearTown);
        Assert.Contains("east of Flagstaff, Arizona", d.Text);
        Assert.NotNull(d.Toward);                 // a town ahead to the east
        Assert.Contains("heading east toward", d.Text);
    }

    [Fact]
    public void WrongTownWithTheSameName_IsRejected_ByTheOtherCities()
    {
        // "Ontario" exists in California and Oregon (and is a Canadian province): the fit must pick the one that agrees.
        var gaz = Gazetteer.For("ats");
        var mem = Learned(gaz, "Los Angeles", "Bakersfield", "Fresno", "Barstow");
        var ont = gaz.FindTowns("Ontario").FirstOrDefault(t => t.Admin == "California");
        if (ont is not null)
        {
            var (x, z) = GameAt(gaz, ont.Lat, ont.Lon);
            mem.Learn("Ontario", x, z);
        }
        var cal = GeoCalibration.Fit(gaz, mem.Cities.Values, CityStates.StateFor);
        Assert.True(cal.IsReady);
        Assert.All(cal.Matches, m => Assert.True(m.Residual.Length() < 4000));
        if (ont is not null) Assert.Contains(cal.Matches, m => m.GameName == "Ontario" && m.Town.Admin == "California");
    }

    [Fact]
    public void Europe_DescribesCountry()
    {
        var gaz = Gazetteer.For("ets2");
        var mem = Learned(gaz, "Berlin", "Hamburg", "Munich", "Frankfurt", "Prague");
        var cal = GeoCalibration.Fit(gaz, mem.Cities.Values);
        Assert.True(cal.IsReady);
        var (x, z) = GameAt(gaz, 50.3, 11.2);   // northern Bavaria / Thuringia border area
        var d = PlaceDescriber.Describe(gaz, cal, x, z, 180, moving: false, mem.Cities.Keys);
        Assert.Equal("Germany", d!.Area);
    }

    [Fact]
    public void NoCalibration_NoDescription()
    {
        var gaz = Gazetteer.For("ats");
        var cal = GeoCalibration.Fit(gaz, Array.Empty<LocationMemory.City>());
        Assert.Null(PlaceDescriber.Describe(gaz, cal, 1000, 2000, 0, true));
    }
}
