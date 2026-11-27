using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using TruckingAdvisor.AI;
using TruckingAdvisor.AI.Streaming;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;
using TruckingAdvisor.Shared.Models;
using TruckingAdvisor.SimHubPlugin;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

/// <summary>The SimHub dashboard package, the panel the dashboard's web view shows, and the data behind it.</summary>
public sealed class SimHubTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static IEnumerable<JsonObject> AllObjects(JsonNode? node)
    {
        if (node is JsonObject o)
        {
            yield return o;
            foreach (var (_, v) in o) foreach (var x in AllObjects(v)) yield return x;
        }
        else if (node is JsonArray a)
            foreach (var v in a) foreach (var x in AllObjects(v)) yield return x;
    }

    private static (JsonObject Dash, JsonObject Meta, string[] Entries) ReadPackage(int port) => ReadPackage(SimHubDashboard.Main, port);

    private static (JsonObject Dash, JsonObject Meta, string[] Entries) ReadPackage(SimHubLayout layout, int port)
    {
        using var ms = new MemoryStream();
        SimHubDashboard.WritePackage(ms, layout, port);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        string Read(string name)
        {
            using var r = new StreamReader(zip.GetEntry(name)!.Open());
            return r.ReadToEnd();
        }
        var entries = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        return (JsonNode.Parse(Read($"{layout.Name}/{layout.Name}.djson"))!.AsObject(),
                JsonNode.Parse(Read($"{layout.Name}/{layout.Name}.djson.metadata"))!.AsObject(), entries);
    }

    public static IEnumerable<object[]> Layouts() => SimHubDashboard.All.Select(l => new object[] { l.Name });
    private static SimHubLayout Layout(string name) => SimHubDashboard.All.Single(l => l.Name == name);

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Package_IsAZipSimHubCanImport(string name)
    {
        var layout = Layout(name);
        var (dash, meta, entries) = ReadPackage(layout, 8790);
        // SimHub's importer takes the first folder holding <folder>/<folder>.djson (+ metadata): one dashboard per package.
        Assert.Equal(new[] { $"{name}/{name}.djson", $"{name}/{name}.djson.metadata" }, entries);
        Assert.Equal(2, (int)dash["Version"]!);
        Assert.Equal(layout.Width, (int)dash["BaseWidth"]!);
        Assert.Equal(layout.Height, (int)dash["BaseHeight"]!);
        Assert.Equal(name, (string)meta["Title"]!);
        Assert.Equal(1, (int)meta["ScreenCount"]!);

        var screen = dash["Screens"]!.AsArray().Single()!.AsObject();
        Assert.True((bool)screen["InGameScreen"]! && (bool)screen["IdleScreen"]!);
        var items = screen["Items"]!.AsArray().Select(i => i!.AsObject()).ToList();
        Assert.True(items.Count > 10);

        // Json.NET only honours $type as the first key; every item carries one, a unique id and a name.
        foreach (var o in AllObjects(dash).Where(o => o.ContainsKey("$type")))
            Assert.Equal("$type", o.First().Key);
        Assert.All(items, i => Assert.StartsWith("SimHub.Plugins.OutputPlugins.GraphicalDash.Models.", (string)i["$type"]!));
        Assert.Equal(items.Count, items.Select(i => (string)i["Id"]!).Distinct().Count());
        Assert.Equal(items.Count, items.Select(i => (string)i["Name"]!).Distinct().Count());
        Assert.All(items, i => Assert.True(Guid.TryParse((string)i["Id"]!, out _)));

        // Every item fits on the dashboard.
        Assert.All(items, i =>
        {
            Assert.InRange((int)i["Left"]!, 0, layout.Width);
            Assert.InRange((int)i["Top"]!, 0, layout.Height);
            Assert.InRange((int)i["Left"]! + (int)i["Width"]!, 0, layout.Width);
            Assert.InRange((int)i["Top"]! + (int)i["Height"]!, 0, layout.Height);
        });
    }

    [Fact]
    public void Package_WebViewOpensThePanelOnTheConfiguredPort_OnTopOfEverything()
    {
        var (dash, _, _) = ReadPackage(9123);
        var items = dash["Screens"]![0]!["Items"]!.AsArray().Select(i => i!.AsObject()).ToList();
        var web = Assert.Single(items, i => ((string)i["$type"]!).Contains("WebPageItem"));
        Assert.Equal("http://localhost:9123/simhub?embed=simhub", (string)web["StartAddress"]!);
        Assert.Same(web, items[^1]); // drawn last: covers the "start TruckingAdvisor" hint once it loads
        Assert.Equal(SimHubDashboard.GaugeColumnWidth, (int)web["Left"]!);
        Assert.Equal(SimHubDashboard.Width, (int)web["Left"]! + (int)web["Width"]!);
    }

    [Fact]
    public void Package_GaugesReadSimHubsOwnTruckData()
    {
        string json = SimHubDashboard.BuildDashboardJson(8789);
        foreach (var prop in new[] { "DataCorePlugin.GameData.SpeedLocal", "DataCorePlugin.GameData.Gear", "DataCorePlugin.GameData.Rpms",
                     "DataCorePlugin.GameData.FuelPercent", "DataCorePlugin.GameRawData.Job.SpeedLimitKmh",
                     "DataCorePlugin.GameRawData.Drivetrain.ParkingBrake", "DataCorePlugin.GameRawData.Lights.BlinkerLeftOn" })
            Assert.Contains($"[{prop}]", json);
        string left = SimHubDashboard.BuildDashboardJson(SimHubDashboard.TripleLeft, 8789), right = SimHubDashboard.BuildDashboardJson(SimHubDashboard.TripleRight, 8789);
        Assert.Contains("[DataCorePlugin.GameData.SpeedLocal]", left);
        Assert.Contains("[DataCorePlugin.GameRawData.Lights.BlinkerLeftOn]", left);
        Assert.Contains("[DataCorePlugin.GameData.Gear]", right);
        Assert.Contains("[DataCorePlugin.GameData.Rpms]", right);
        Assert.Contains("[DataCorePlugin.GameRawData.Lights.BlinkerRightOn]", right);
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Package_FormulasAreBalanced_AndBindRealTargets(string name)
    {
        var dash = JsonNode.Parse(SimHubDashboard.BuildDashboardJson(Layout(name), 8789))!;
        var formulas = AllObjects(dash).Where(o => o.ContainsKey("Expression")).Select(o => (string)o["Expression"]!).Where(e => e.Length > 0).ToList();
        Assert.NotEmpty(formulas);
        foreach (var f in formulas)
        {
            Assert.Equal(f.Count(c => c == '('), f.Count(c => c == ')'));
            Assert.Equal(f.Count(c => c == '['), f.Count(c => c == ']'));
            Assert.Equal(0, f.Count(c => c == '\'') % 2);
        }
        // Bindings are formulas (mode 2) on real targets.
        foreach (var b in AllObjects(dash).Where(o => o.ContainsKey("Bindings")).Select(o => o["Bindings"]!.AsObject()))
            foreach (var (target, binding) in b)
            {
                Assert.Contains(target, new[] { "Text", "TextColor", "Visible", "BackgroundColor", "Value", "GaugeColor", "SweepColor" });
                Assert.Equal(2, (int)binding!["Mode"]!);
                var formula = binding["Formula"]!.AsObject();
                if (formula.ContainsKey("Interpreter"))
                {
                    // SimHub's JavaScript: Interpreter 1, and a formula must return its value.
                    Assert.Equal(1, (int)formula["Interpreter"]!);
                    Assert.Contains("return ", (string)formula["Expression"]!);
                }
            }
    }

    [Fact]
    public void Triple_RoundsAreSquare_TheCentreIsA4InchPanel_AndAllButTheCentreWorkWithoutTheApp()
    {
        Assert.Equal(new[] { "TruckingAdvisor Triple Left", "TruckingAdvisor Triple Center", "TruckingAdvisor Triple Right" },
            SimHubDashboard.Triple.Select(l => l.Name));
        // The plugin switches screens to these names: the two lists must agree.
        Assert.Equal(TriplePlan.Dashboards, SimHubDashboard.Triple.Select(l => l.Name));
        Assert.Equal(SimHubDashboard.TripleLeft.Width, SimHubDashboard.TripleLeft.Height);
        Assert.Equal(SimHubDashboard.TripleRight.Width, SimHubDashboard.TripleRight.Height);
        Assert.Equal((800, 480), (SimHubDashboard.TripleCenter.Width, SimHubDashboard.TripleCenter.Height));

        foreach (var layout in SimHubDashboard.Triple)
        {
            string json = SimHubDashboard.BuildDashboardJson(layout, 9100);
            // Drawn with SimHub's own items only (VoCore screens are rendered to a bitmap: no web view)...
            Assert.DoesNotContain("WebPageItem", json);
            // ...and TruckingAdvisor's data comes from the compact endpoint on the configured port.
            Assert.Contains("downloadstringasync(400, 'http://localhost:9100/simhub/dash.json')", json);
        }
        var round = JsonNode.Parse(SimHubDashboard.BuildDashboardJson(SimHubDashboard.TripleLeft, 8789))!;
        Assert.Contains(AllObjects(round), o => ((string?)o["$type"])?.Contains("ArcGaugeItem") == true);
    }

    [Fact]
    public void CompactState_IsSmallFlatData_FromThePanelState()
    {
        Assert.False((bool)SimHubDashboard.CompactState(null)["ok"]!);
        Assert.False((bool)SimHubDashboard.CompactState(JsonNode.Parse("{\"available\":false}"))["ok"]!);

        var panel = JsonNode.Parse("""
            {"available":true,"connection":{"live":true},"gameTime":"Tue 14:20","location":"near Flagstaff, AZ","gradeAhead":"",
             "trip":{"active":true,"cargo":"Lumber","destination":"Albuquerque, NM","distanceLeft":"264 mi","eta":"4h 05m","progress":38.26,
                     "score":"91","scoreValue":91,"rating":"Professional Driver"},
             "radio":{"channel":19,"channelName":"HIGHWAY TRAFFIC","mode":"rx","messages":[{"handle":"Big Red","text":"old"},
                      {"handle":"Sarah (Dispatch)","text":"Checking in.","dispatch":true,"mine":false}]},
             "coaching":[{"severity":"High","message":"Hard braking","ageSeconds":10}],
             "career":{"level":"LEVEL 7"}}
            """);
        var c = SimHubDashboard.CompactState(panel);
        Assert.True((bool)c["ok"]!);
        Assert.Equal("91", (string)c["score"]!);
        Assert.Equal(19, (int)c["ch"]!);
        Assert.Equal("RX", (string)c["radio"]!);
        Assert.Equal("Sarah (Dispatch)", (string)c["who"]!);
        Assert.True((bool)c["dispatch"]!);
        Assert.Equal("Albuquerque, NM", (string)c["dest"]!);
        Assert.Equal(38.3, (double)c["progress"]!, 1);
        Assert.Equal("Hard braking", (string)c["alert"]!);
        Assert.Equal("bad", (string)c["alertLevel"]!);
        Assert.True(c.ToJsonString().Length < 800);

        // A grade ahead wins over coaching; old coaching is not repeated.
        panel["gradeAhead"] = "Steep downgrade ahead";
        Assert.Equal(("Steep downgrade ahead", "warn"), ((string)SimHubDashboard.CompactState(panel)["alert"]!, (string)SimHubDashboard.CompactState(panel)["alertLevel"]!));
        panel["gradeAhead"] = "";
        panel["coaching"]![0]!["ageSeconds"] = 300;
        Assert.Equal("", (string)SimHubDashboard.CompactState(panel)["alert"]!);
    }

    // ---------- the SimHub plugin's decisions ----------

    private static ScreenInfo Screen(string key, string name, int w, int h, string dash = "Mine") =>
        new() { Key = key, Name = name, Width = w, Height = h, Dashboard = dash };

    [Fact]
    public void Plugin_GivesTheRoundsAndThe4InchTheTripleSet_AndLeavesOtherScreensAlone()
    {
        var screens = new[]
        {
            Screen("b", "VoCore Round B", 800, 800),
            Screen("c", "VoCore 4in", 800, 480),
            Screen("a", "VoCore Round A", 800, 800),
            Screen("m", "Main monitor", 1920, 1080),
            Screen("x", "Not connected", 0, 0)
        };
        var plan = TriplePlan.Assign(screens, swapRounds: false);
        Assert.Equal(TriplePlan.Left, plan["a"]);
        Assert.Equal(TriplePlan.Right, plan["b"]);
        Assert.Equal(TriplePlan.Center, plan["c"]);
        Assert.False(plan.ContainsKey("m"));
        Assert.False(plan.ContainsKey("x"));

        var swapped = TriplePlan.Assign(screens, swapRounds: true);
        Assert.Equal(TriplePlan.Right, swapped["a"]);
        Assert.Equal(TriplePlan.Left, swapped["b"]);

        // A rotated 4" (480 x 800) is still the centre; one round alone is the left one.
        var two = TriplePlan.Assign(new[] { Screen("r", "Round", 480, 480), Screen("p", "Portrait 4in", 480, 800) }, swapRounds: true);
        Assert.Equal(TriplePlan.Left, two["r"]);
        Assert.Equal(TriplePlan.Center, two["p"]);
    }

    [Fact]
    public void Plugin_RemembersThePreviousDashboards_ThroughAFile()
    {
        var previous = new Dictionary<string, string> { ["guid-1#0"] = "My Round Dash", ["guid-2#0"] = "", ["odd\tkey"] = "Dash\nwith\\escapes" };
        var back = TriplePlan.Deserialize(TriplePlan.Serialize(previous));
        Assert.Equal(previous.OrderBy(k => k.Key), back.OrderBy(k => k.Key));
        Assert.Empty(TriplePlan.Deserialize(""));
        Assert.True(TriplePlan.IsOurs("truckingadvisor triple left"));
        Assert.False(TriplePlan.IsOurs("My Round Dash"));
    }

    [Fact]
    public void Plugin_ReadsTruckingAdvisorsAnswer_AndItsPort()
    {
        var on = TriplePlan.ParseAppState("{\"ok\":true,\"score\":\"91\",\"autoTriple\":true,\"swapRounds\":true}");
        Assert.True(on.Ok && on.AutoTriple && on.SwapRounds);
        var off = TriplePlan.ParseAppState("{\"ok\":true,\"autoTriple\": false}");
        Assert.True(off.Ok);
        Assert.False(off.AutoTriple);
        Assert.False(TriplePlan.ParseAppState("{\"ok\":false}").Ok);
        Assert.False(TriplePlan.ParseAppState(null).Ok);
        Assert.False(TriplePlan.ParseAppState("ERROR: refused").Ok);

        Assert.Equal(9000, TriplePlan.ParsePort("# comment\nport=9000\n"));
        Assert.Equal(8789, TriplePlan.ParsePort("port=nope"));
        Assert.Equal(8789, TriplePlan.ParsePort(null));
    }

    [Fact]
    public void Package_IsTheSameEveryTime()
    {
        Assert.Equal(SimHubDashboard.BuildDashboardJson(8789), SimHubDashboard.BuildDashboardJson(8789));
        Assert.NotEqual(SimHubDashboard.BuildDashboardJson(8789), SimHubDashboard.BuildDashboardJson(8790));
    }

    [Fact]
    public async Task Server_ServesThePanel_ItsState_AndTheDashboard()
    {
        int port = FreePort();
        object? panel = null;
        await using var server = new StreamOverlayServer(port, () => new { channel = 19 }) { PanelState = () => panel };
        server.Start();
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };

        string html = await http.GetStringAsync("/simhub");
        Assert.Contains("/simhub/state.json", html);
        Assert.Contains("TruckingAdvisor Panel", html);

        // Switched off: the page is told so (and says how to switch it on).
        Assert.False(JsonDocument.Parse(await http.GetStringAsync("/simhub/state.json")).RootElement.GetProperty("available").GetBoolean());

        panel = new { available = true, trip = new { score = "91" } };
        var state = JsonDocument.Parse(await http.GetStringAsync("/simhub/state.json")).RootElement;
        Assert.True(state.GetProperty("available").GetBoolean());
        Assert.Equal("91", state.GetProperty("trip").GetProperty("score").GetString());

        var compact = JsonDocument.Parse(await http.GetStringAsync("/simhub/dash.json")).RootElement;
        Assert.True(compact.GetProperty("ok").GetBoolean());
        Assert.Equal("91", compact.GetProperty("score").GetString());
        var triple = await http.GetAsync("/simhub/TruckingAdvisor%20Triple%20Left.simhubdash");
        Assert.Equal(HttpStatusCode.OK, triple.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/simhub/Nope.simhubdash")).StatusCode);

        var resp = await http.GetAsync("/simhub/TruckingAdvisor.simhubdash");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("attachment", resp.Content.Headers.ContentDisposition?.DispositionType);
        using var zip = new ZipArchive(new MemoryStream(await resp.Content.ReadAsByteArrayAsync()));
        using var r = new StreamReader(zip.GetEntry("TruckingAdvisor/TruckingAdvisor.djson")!.Open());
        Assert.Contains($"http://localhost:{port}/simhub?embed=simhub", await r.ReadToEndAsync());
    }

    private static CBRadioService Radio(AiSettings settings, VirtualCompanyProfile company) =>
        new(PersonalityCatalog.CreateDefaultRoster(), company, new ViewModelThreadingTests.FakeAudio(), () => new AtsTelemetrySnapshot(),
            settings, llm: null, random: new Random(1));

    [Fact]
    public async Task Hub_RunsTheServerForTheSimHubPanelAlone_AndOnlyServesItsDataWhileItIsOn()
    {
        var settings = new AiSettings();
        settings.Stream.OverlayPort = FreePort();
        settings.Stream.SimHubPanelEnabled = true;
        var company = new VirtualCompanyProfile();
        await using var hub = new StreamHub(Radio(settings, company), settings, company, () => new AtsTelemetrySnapshot())
        {
            SimHubPanelState = () => new { available = true, gameTime = "Tue 14:20" }
        };
        await hub.ApplyAsync();

        Assert.True(hub.SimHubPanelServing);
        Assert.Equal($"http://localhost:{settings.Stream.OverlayPort}/simhub", hub.SimHubPanelUrl);
        Assert.Equal("Overlay is off.", hub.OverlayStatus);
        Assert.Contains("SimHub panel ready", hub.SimHubStatus);
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{settings.Stream.OverlayPort}") };
        Assert.Contains("Tue 14:20", await http.GetStringAsync("/simhub/state.json"));
        // The SimHub plugin's switches ride along on dash.json.
        settings.Stream.SimHubSwapRounds = true;
        var flags = JsonDocument.Parse(await http.GetStringAsync("/simhub/dash.json")).RootElement;
        Assert.True(flags.GetProperty("autoTriple").GetBoolean());
        Assert.True(flags.GetProperty("swapRounds").GetBoolean());

        // The overlay on as well: still one server, same port, both work.
        settings.Stream.OverlayEnabled = true;
        await hub.ApplyAsync();
        Assert.Contains("Overlay ready", hub.OverlayStatus);
        Assert.Contains("Tue 14:20", await http.GetStringAsync("/simhub/state.json"));

        // Panel off, overlay on: the server stays for the overlay but no longer hands out the panel's data.
        settings.Stream.SimHubPanelEnabled = false;
        await hub.ApplyAsync();
        Assert.False(hub.SimHubPanelServing);
        Assert.Equal("The SimHub panel is off.", hub.SimHubStatus);
        Assert.DoesNotContain("14:20", await http.GetStringAsync("/simhub/state.json"));

        settings.Stream.OverlayEnabled = false;
        await hub.ApplyAsync();
        Assert.Null(hub.OverlayUrl);
    }

    [Fact]
    public void ViewModel_PanelState_CarriesTheDeliveryRadioAndCareer_AsPlainData()
    {
        var telemetry = new ViewModelThreadingTests.FakeTelemetry();
        var settings = new AiSettings();
        var company = new VirtualCompanyProfile();
        var radio = Radio(settings, company);
        var vm = new TruckingAdvisor.Presentation.MainDashboardViewModel(telemetry, new TruckingAdvisor.Core.Coaching.DrivingAnalyzer(),
            new TruckingAdvisor.Core.Advisors.FuelAndMaintenanceAdvisor(), new TruckingAdvisor.Core.Trips.TripTracker(),
            new ViewModelThreadingTests.FakeAudio(), radio, company, new CareerProfile(), settings, postToUi: a => a(), db: null);

        telemetry.Publish(new AtsTelemetrySnapshot
        {
            ConnectionState = ConnectionState.Connected,
            SpeedMph = 61,
            SpeedLimitMph = 55,
            JobActive = true,
            CargoName = "Lumber",
            OriginCity = "Phoenix",
            DestinationCity = "Tucson",
            Timestamp = DateTimeOffset.UtcNow
        });

        var json = JsonDocument.Parse(JsonSerializer.Serialize(vm.BuildSimHubPanelState(), new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;
        Assert.True(json.GetProperty("available").GetBoolean());
        Assert.True(json.GetProperty("connection").GetProperty("live").GetBoolean());
        Assert.Equal("61", json.GetProperty("truck").GetProperty("speed").GetString());
        Assert.True(json.GetProperty("truck").GetProperty("speeding").GetBoolean());
        Assert.Contains("Lumber", json.GetProperty("trip").GetProperty("cargo").GetString());
        Assert.Contains("Tucson", json.GetProperty("trip").GetProperty("route").GetString());
        Assert.Equal(19, json.GetProperty("radio").GetProperty("channel").GetInt32());
        Assert.Equal(JsonValueKind.Array, json.GetProperty("radio").GetProperty("messages").ValueKind);
        Assert.StartsWith("LEVEL", json.GetProperty("career").GetProperty("level").GetString());
        vm.Dispose();
    }
}
