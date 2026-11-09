using TruckingAdvisor.AI;
using TruckingAdvisor.Data;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

public sealed class DataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ta-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Database_RoundTripsTripsAndTruckerMemories()
    {
        var db = new TruckingAdvisorDatabase(Path.Combine(_dir, "t.db"));
        var trip = new DeliveryTripRecord { Cargo = "Lumber", DistanceMiles = 420, State = DeliveryState.Completed };
        db.SaveTrip(trip);
        Assert.Equal("Lumber", db.LoadTrips().Single().Cargo);

        var roster = PersonalityCatalog.CreateDefaultRoster();
        roster[0].RelationshipPoints = 50;
        roster[0].Relationship = RelationshipLevel.RoadAcquaintance;
        roster[0].SummarizedMemories.Add("talked about Denver");
        db.SaveTrucker(roster[0]);

        var merged = db.MergeWithSaved(PersonalityCatalog.CreateDefaultRoster());
        Assert.Equal(RelationshipLevel.RoadAcquaintance, merged[0].Relationship);
        Assert.Equal("talked about Denver", merged[0].SummarizedMemories.Single());
        Assert.Equal(RelationshipLevel.Stranger, merged[1].Relationship);

        var company = db.LoadCompany();
        Assert.Equal(0, company.TotalDeliveries);
        company.PlayerHandle = "Highway Ghost";
        db.SaveCompany(company);
        Assert.Equal("Highway Ghost", db.LoadCompany().PlayerHandle);
    }

    [Fact]
    public void Csv_EscapesQuotesCommasAndFormulas()
    {
        var csv = TruckingAdvisorDatabase.ExportDeliveryReportCsv(new[]
        {
            new DeliveryTripRecord { Cargo = "Pipe, \"steel\"", Origin = "=HYPERLINK(\"x\")", Destination = "Denver" }
        });
        Assert.Contains("\"Pipe, \"\"steel\"\"\"", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv);
    }

    [Fact]
    public void TextReport_NotesPartialTrips()
    {
        var text = TruckingAdvisorDatabase.ExportDeliveryReportText(new DeliveryTripRecord { StartedMidDelivery = true });
        Assert.Contains("started mid-delivery", text);
        Assert.Contains("Economy: n/a", text);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }
}
