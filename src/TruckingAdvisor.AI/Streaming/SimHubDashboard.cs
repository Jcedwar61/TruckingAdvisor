using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TruckingAdvisor.AI.Streaming;

/// <summary>One SimHub dashboard: its name (also its folder and file name in SimHub), size and items.</summary>
/// <param name="Items">Builds the items for a TruckingAdvisor panel served on the given port.</param>
public sealed record SimHubLayout(string Name, string Description, int Width, int Height, Func<int, IEnumerable<JsonObject>> Items)
{
    /// <summary>The package's file name, e.g. "TruckingAdvisor.simhubdash".</summary>
    public string FileName => Name + ".simhubdash";
}

/// <summary>
/// Builds TruckingAdvisor's dashboards for SimHub (Dash Studio) as <c>.simhubdash</c> packages: a zip holding
/// <c>NAME/NAME.djson</c> and its <c>.djson.metadata</c>, one dashboard per package (SimHub's importer takes the
/// first folder with a matching .djson). Opening the file in Windows hands it to SimHub, which imports it.
///
/// <list type="bullet">
/// <item><see cref="Main"/> (1280 x 720, any monitor, tablet or phone): SimHub's own gauges beside SimHub's web
/// view showing TruckingAdvisor's live panel (<c>http://localhost:PORT/simhub</c>).</item>
/// <item><see cref="TripleLeft"/>, <see cref="TripleCenter"/>, <see cref="TripleRight"/>: a set for a triple-screen
/// DDU with two round screens either side of a 4" one (such as the DaSIMSIMMA triple, on VoCore screens): speed on
/// the left round, gear and RPM on the right, CB radio, driver score and delivery in the middle. These are drawn
/// only with SimHub's own items (VoCore screens are rendered to a bitmap, where a web view can't be relied on) and
/// fetch TruckingAdvisor's data themselves with SimHub's <c>downloadstringasync</c> from <c>/simhub/dash.json</c>.</item>
/// </list>
///
/// Truck readouts always come from SimHub's own ATS / ETS2 telemetry, so they run at SimHub's frame rate and work
/// with TruckingAdvisor closed. The JSON follows what Dash Studio writes (Json.NET, <c>$type</c> first in every item).
/// Ids are derived from names, so the same settings always produce the same files.
/// </summary>
public static class SimHubDashboard
{
    public const string Name = "TruckingAdvisor";
    public const int Width = 1280;
    public const int Height = 720;
    /// <summary>Width of SimHub's own gauge column on <see cref="Main"/>; the TruckingAdvisor panel fills the rest.</summary>
    public const int GaugeColumnWidth = 440;
    /// <summary>The round screens' dashboards are square; SimHub scales them to the screen (800 x 800 on a 3.4" VoCore round).</summary>
    public const int RoundSize = 800;

    private const string Models = "SimHub.Plugins.OutputPlugins.GraphicalDash.Models.";
    private const string TextType = Models + "TextItem, SimHub.Plugins";
    private const string RectType = Models + "RectangleItem, SimHub.Plugins";
    private const string GaugeType = Models + "LinearGaugeItem, SimHub.Plugins";
    private const string ArcType = Models + "ArcGaugeItem, SimHub.Plugins";
    private const string WebType = Models + "WebPageItem, SimHub.Plugins";

    // Colours (#AARRGGBB, as SimHub stores them).
    private const string Bg = "#FF0B1020";
    private const string Card = "#FF141B2D";
    private const string Edge = "#FF26314A";
    private const string Track = "#FF1E293B";
    private const string Text = "#FFF1F5F9";
    private const string Muted = "#FF94A3B8";
    private const string Dim = "#FF475569";
    private const string Off = "#FF334155";
    private const string Accent = "#FFF59E0B";
    private const string Ok = "#FF22C55E";
    private const string Bad = "#FFEF4444";
    private const string Info = "#FF38BDF8";
    private const string Lcd = "#FF6DFF9D";
    private const string LcdBg = "#FF07140C";
    private const string Clear = "#00FFFFFF";

    // ---- SimHub properties (NCalc). GameData.* is SimHub's game-neutral data; GameRawData.* is the SCS plugin's. ----
    private const string SpeedLocal = "isnull([DataCorePlugin.GameData.SpeedLocal], 0)";
    private const string SpeedKmh = "isnull([DataCorePlugin.GameData.SpeedKmh], 0)";
    private const string IsMph = "isnull([DataCorePlugin.GameData.SpeedLocalUnit], '') = 'MPH'";
    private const string LimitKmh = "isnull([DataCorePlugin.GameRawData.Job.SpeedLimitKmh], 0)";
    private const string CruiseOn = "isnull([DataCorePlugin.GameRawData.Drivetrain.CruiseControl], false)";
    private const string CruiseKmh = "isnull([DataCorePlugin.GameRawData.Drivetrain.CruiseControlSpeedKmh], 0)";
    private const string Rpm = "isnull([DataCorePlugin.GameData.Rpms], 0)";
    private const string MaxRpm = "isnull([DataCorePlugin.GameData.MaxRpm], 0)";
    private const string Fuel = "isnull([DataCorePlugin.GameData.FuelPercent], 0)";
    private const string GameRunning = "isnull([DataCorePlugin.GameRunning], false)";
    private const string Gear = "isnull([DataCorePlugin.GameData.Gear], 'N')";
    private const string ParkBrake = "isnull([DataCorePlugin.GameRawData.Drivetrain.ParkingBrake], false)";
    private const string FuelLight = "isnull([DataCorePlugin.GameRawData.Drivetrain.FuelWarningLight], false)";
    private static string Raw(string prop) => $"isnull([DataCorePlugin.GameRawData.{prop}], false)";
    private static readonly string SpeedBad = $"{LimitKmh} > 0 and {SpeedKmh} > {LimitKmh} + 1.6";
    private static readonly string HasLimit = $"{LimitKmh} > 0";

    /// <summary>A km/h value in the speed unit SimHub is set to (mph or km/h), rounded.</summary>
    private static string InLocalUnit(string kmh) => $"if({IsMph}, round(({kmh}) / 1.609344, 0), round({kmh}, 0))";
    private static string RpmPercent => $"if({MaxRpm} > 0, {Rpm} * 100 / {MaxRpm}, 0)";

    /// <summary>The address SimHub's web view opens on <see cref="Main"/>: the panel, without the readouts SimHub already draws.</summary>
    public static string PanelUrl(int port) => $"http://localhost:{port}/simhub?embed=simhub";

    /// <summary>The compact JSON the triple-screen dashboards read with <c>downloadstringasync</c>.</summary>
    public static string CompactUrl(int port) => $"http://localhost:{port}/simhub/dash.json";

    /// <summary>
    /// A JavaScript binding that reads TruckingAdvisor's compact state (<c>d</c>) and returns <paramref name="expr"/>,
    /// or <paramref name="fallback"/> while TruckingAdvisor (or its SimHub panel) is off. downloadstringasync keeps one
    /// cached copy per address for the whole dashboard and refreshes it in the background, so this costs one small
    /// request every 400 ms however many items use it.
    /// </summary>
    private static string Ta(int port, string expr, string fallback = "''") =>
        "js:var s = downloadstringasync(400, '" + CompactUrl(port) + "');\n" +
        "var d = null;\n" +
        "try { if (s && s.charAt(0) == '{') d = JSON.parse(s); } catch (e) { }\n" +
        $"if (!d || !d.ok) return {fallback};\n" +
        $"return {expr};";

    // ---- The dashboards ----

    public static readonly SimHubLayout Main = new(Name,
        "American Truck Simulator / Euro Truck Simulator 2 dash: SimHub's speed, limit, gear, RPM, fuel and telltales beside " +
        "TruckingAdvisor's live panel (driver score, delivery, CB radio, coaching, career). The panel needs TruckingAdvisor " +
        "running with its SimHub panel switched on.",
        Width, Height, MainItems);

    public static readonly SimHubLayout TripleLeft = new($"{Name} Triple Left",
        "Left round screen of a triple-screen DDU (e.g. DaSIMSIMMA): speedometer ring, speed limit, cruise, left blinker and " +
        "your TruckingAdvisor driver score. ATS / ETS2.",
        RoundSize, RoundSize, LeftItems);

    public static readonly SimHubLayout TripleCenter = new($"{Name} Triple Center",
        "Centre 4\" screen of a triple-screen DDU (e.g. DaSIMSIMMA): TruckingAdvisor's CB radio (channel and the last line " +
        "heard), driver score, delivery progress and warnings. Needs TruckingAdvisor running with its SimHub panel switched on.",
        800, 480, CenterItems);

    public static readonly SimHubLayout TripleRight = new($"{Name} Triple Right",
        "Right round screen of a triple-screen DDU (e.g. DaSIMSIMMA): RPM ring, gear, fuel ring, park brake and air " +
        "warnings, right blinker and your TruckingAdvisor CB channel. ATS / ETS2.",
        RoundSize, RoundSize, RightItems);

    /// <summary>The triple-screen set, left to right.</summary>
    public static readonly IReadOnlyList<SimHubLayout> Triple = new[] { TripleLeft, TripleCenter, TripleRight };
    public static readonly IReadOnlyList<SimHubLayout> All = new[] { Main, TripleLeft, TripleCenter, TripleRight };

    /// <summary>The layout whose package file name is <paramref name="fileName"/> (case-insensitive), or null.</summary>
    public static SimHubLayout? FindByFileName(string fileName) =>
        All.FirstOrDefault(l => string.Equals(l.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    // ---- Packages ----

    /// <summary>Writes <see cref="Main"/> as a <c>.simhubdash</c> package for a panel served on <paramref name="port"/>.</summary>
    public static void WritePackage(Stream output, int port) => WritePackage(output, Main, port);

    public static void WritePackage(Stream output, SimHubLayout layout, int port)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Add(string name, string content)
        {
            var entry = zip.CreateEntry($"{layout.Name}/{name}", CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        Add($"{layout.Name}.djson", BuildDashboardJson(layout, port));
        Add($"{layout.Name}.djson.metadata", BuildMetadata(layout).ToJsonString(Indented));
    }

    /// <summary>Writes the package to a file (overwriting it) and returns the path.</summary>
    public static string WritePackage(string path, int port) => WritePackage(path, Main, port);

    public static string WritePackage(string path, SimHubLayout layout, int port)
    {
        using (var f = File.Create(path)) WritePackage(f, layout, port);
        return path;
    }

    // Readable in a text editor and in Dash Studio's formula editor: quotes and < > & are written as they are.
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The <c>.djson</c> text of <see cref="Main"/>.</summary>
    public static string BuildDashboardJson(int port) => BuildDashboardJson(Main, port);

    public static string BuildDashboardJson(SimHubLayout layout, int port)
    {
        var items = layout.Items(port).ToList();
        foreach (var item in items)
        {
            // Ids come from the dashboard and item names, so every dashboard's ids are its own.
            item["Id"] = StableGuid($"{layout.Name}/item/{item["Name"]}");
        }
        var screen = new JsonObject
        {
            ["RenderingSkip"] = 0,
            ["Name"] = "Main",
            ["InGameScreen"] = true,
            ["IdleScreen"] = true,
            ["PitScreen"] = true,
            ["ScreenId"] = StableGuid($"{layout.Name}/screen/Main"),
            ["AllowOverlays"] = true,
            ["IsForegroundLayer"] = false,
            ["IsOverlayLayer"] = false,
            ["OverlayTriggerExpression"] = new JsonObject { ["Expression"] = "" },
            ["ScreenEnabledExpression"] = new JsonObject { ["Expression"] = "" },
            ["OverlayMaxDuration"] = 0,
            ["OverlayMinDuration"] = 0,
            ["IsBackgroundLayer"] = false,
            ["BackgroundColor"] = layout.Width == layout.Height ? "#FF000000" : Bg,
            ["Background"] = "None",
            ["MinimumRefreshIntervalMS"] = 0,
            ["Items"] = new JsonArray(items.ToArray<JsonNode?>())
        };
        var dash = new JsonObject
        {
            ["Version"] = 2,
            ["Id"] = StableGuid($"{layout.Name}/dashboard"),
            ["BaseHeight"] = layout.Height,
            ["BaseWidth"] = layout.Width,
            ["BackgroundColor"] = (string)screen["BackgroundColor"]!,
            ["Screens"] = new JsonArray(screen),
            ["SnapToGrid"] = true,
            ["HideLabels"] = false,
            ["ShowForeground"] = true,
            ["ForegroundOpacity"] = 100,
            ["ShowBackground"] = true,
            ["BackgroundOpacity"] = 100,
            ["ShowBoundingRectangles"] = true,
            ["GridSize"] = 5,
            ["Images"] = new JsonArray(),
            ["Metadata"] = BuildMetadata(layout),
            ["ShowOnScreenControls"] = true,
            ["IsOverlay"] = false,
            ["EnableClickThroughOverlay"] = true,
            ["EnableOnDashboardMessaging"] = false,
            ["UseStrictJSIsolation"] = true,
            ["UseStrictJSIsolationWarning"] = false
        };
        return dash.ToJsonString(Indented);
    }

    private static JsonObject BuildMetadata(SimHubLayout layout) => new()
    {
        ["Category"] = "Trucks",
        ["Title"] = layout.Name,
        ["Description"] = layout.Description,
        ["Author"] = "TruckingAdvisor",
        ["Width"] = layout.Width,
        ["Height"] = layout.Height,
        ["ScreenCount"] = 1,
        ["InGameScreensIndexs"] = new JsonArray(0),
        ["IdleScreensIndexs"] = new JsonArray(0),
        ["PitScreensIndexs"] = new JsonArray(0),
        ["MainPreviewIndex"] = 0,
        ["IsOverlay"] = false,
        ["ShowInTaskBar"] = true,
        ["OverlaySizeWarning"] = true,
        ["MetadataVersion"] = 2,
        ["EnableOnDashboardMessaging"] = false
    };

    // ---- The compact state the triple-screen dashboards read ----

    /// <summary>
    /// Flattens the SimHub panel's state (as served at /simhub/state.json) into the few short fields the
    /// triple-screen dashboards show, so each SimHub formula parses a small object. <c>ok</c> is false while the panel
    /// is off. Truck readouts are left out: SimHub has them itself.
    /// </summary>
    public static JsonObject CompactState(JsonNode? panel)
    {
        if (panel is not JsonObject p || p["available"]?.GetValue<bool>() != true) return new JsonObject { ["ok"] = false };
        static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : n?.ToString() ?? "";
        static double D(JsonNode? n) => n is JsonValue v && double.TryParse(v.ToJsonString(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;
        var trip = p["trip"] as JsonObject;
        var radio = p["radio"] as JsonObject;
        var last = (radio?["messages"] as JsonArray)?.LastOrDefault() as JsonObject;

        string alert = S(p["gradeAhead"]), level = alert.Length > 0 ? "warn" : "";
        if (alert.Length == 0 && (p["coaching"] as JsonArray)?.FirstOrDefault() is JsonObject c && D(c["ageSeconds"]) < 45)
        {
            alert = S(c["message"]);
            level = S(c["severity"]) is "Critical" or "High" ? "bad" : "warn";
        }
        string mode = S(radio?["mode"]);
        return new JsonObject
        {
            ["ok"] = true,
            ["live"] = p["connection"]?["live"]?.GetValue<bool>() == true,
            ["time"] = S(p["gameTime"]),
            ["location"] = S(p["location"]),
            ["score"] = S(trip?["score"]),
            ["scoreValue"] = D(trip?["scoreValue"]),
            ["rating"] = S(trip?["rating"]),
            ["active"] = trip?["active"]?.GetValue<bool>() == true,
            ["cargo"] = S(trip?["cargo"]),
            ["dest"] = S(trip?["destination"]),
            ["left"] = S(trip?["distanceLeft"]),
            ["eta"] = S(trip?["eta"]),
            ["progress"] = Math.Round(D(trip?["progress"]), 1),
            ["ch"] = radio?["channel"]?.GetValue<int>() ?? 19,
            ["chName"] = S(radio?["channelName"]),
            ["radio"] = mode switch { "tx" => "TX", "rx" => "RX", "busy" => "...", "off" => "OFF", _ => "READY" },
            ["radioMode"] = mode,
            ["who"] = S(last?["handle"]),
            ["line"] = S(last?["text"]),
            ["dispatch"] = last?["dispatch"]?.GetValue<bool>() == true,
            ["mine"] = last?["mine"]?.GetValue<bool>() == true,
            ["alert"] = alert,
            ["alertLevel"] = level,
            ["level"] = S(p["career"]?["level"])
        };
    }

    // ---- Main (1280 x 720): SimHub's gauges + TruckingAdvisor's panel in the web view ----

    private static IEnumerable<JsonObject> MainItems(int port)
    {
        const int pad = 20, col = GaugeColumnWidth - 2 * pad;

        // Column background and header.
        yield return Rect("ColumnBg", 0, 0, GaugeColumnWidth, Height, Card, border: (Edge, 0, 0, 0, 1));
        yield return Label("Brand", "TRUCKINGADVISOR", pad, 14, 250, 24, 15, Accent, bold: true);
        yield return Label("GameState", "WAITING FOR GAME", 230, 14, 190, 24, 13, Muted, align: 2,
            text: $"if({GameRunning}, ucase(isnull([DataCorePlugin.CurrentGame], '')), 'WAITING FOR GAME')",
            color: $"if({GameRunning}, '{Ok}', '{Muted}')");

        // Speed and unit.
        yield return Label("Speed", "0", pad, 44, 270, 150, 140, Text, bold: true, align: 0,
            text: $"format({SpeedLocal}, '0')", color: $"if({SpeedBad}, '{Bad}', '{Text}')");
        yield return Label("SpeedUnit", "MPH", pad + 4, 190, 200, 26, 18, Muted, bold: true,
            text: $"if({IsMph}, 'MPH', 'KM/H')");

        // Speed-limit sign (white, like the US / EU signs both are); hidden when the game reports no limit.
        foreach (var i in LimitSign("Limit", 312, 58, 108, 132)) yield return i;

        // Gear, cruise and park brake.
        yield return Rect("GearBox", pad, 236, 180, 156, Bg, radius: 12, border: (Edge, 1, 1, 1, 1));
        yield return Label("GearCaption", "GEAR", pad, 244, 180, 20, 13, Muted, bold: true, align: 1);
        yield return Label("Gear", "N", pad, 262, 180, 120, 92, Text, bold: true, align: 1, text: Gear);

        yield return Rect("CruiseBox", 216, 236, 204, 72, Bg, radius: 12, border: (Edge, 1, 1, 1, 1),
            fill: $"if({CruiseOn}, '#FF0F2A1A', '{Bg}')");
        yield return Label("Cruise", "CRUISE OFF", 216, 236, 204, 72, 22, Muted, bold: true, align: 1,
            text: CruiseText, color: $"if({CruiseOn}, '{Ok}', '{Muted}')");

        yield return Rect("ParkBox", 216, 320, 204, 72, Bg, radius: 12, border: (Edge, 1, 1, 1, 1),
            fill: $"if({ParkBrake}, '#FF3A1212', '{Bg}')");
        yield return Label("Park", "PARK BRAKE", 216, 320, 204, 72, 22, Muted, bold: true, align: 1,
            color: $"if({ParkBrake}, '{Bad}', '{Dim}')");

        // RPM bar.
        yield return Label("RpmCaption", "RPM", pad, 412, 100, 22, 14, Muted, bold: true);
        yield return Label("RpmValue", "0", 220, 412, 200, 22, 14, Text, bold: true, align: 2, text: $"format({Rpm}, '0')");
        yield return Gauge("RpmBar", pad, 438, col, 22, value: RpmPercent, color: $"if({RpmPercent} >= 92, '{Bad}', '{Accent}')");

        // Fuel bar.
        yield return Label("FuelCaption", "FUEL", pad, 476, 100, 22, 14, Muted, bold: true);
        yield return Label("FuelValue", "0%", 220, 476, 200, 22, 14, Text, bold: true, align: 2, text: $"format({Fuel}, '0') + '%'");
        yield return Gauge("FuelBar", pad, 502, col, 22, value: Fuel, color: $"if({Fuel} <= 15, '{Bad}', '{Ok}')");

        // Telltales: blinkers, air, water.
        int y = 546, w = 92, gap = (col - 4 * w) / 3;
        var tells = new (string Name, string Text, string Prop, string On)[]
        {
            ("TellLeft", "◀", "Lights.BlinkerLeftOn", Ok),
            ("TellAir", "AIR", "Axilliary.AirPressureWarning", Bad),
            ("TellWater", "WATER", "Axilliary.WaterTemperatureWarning", Bad),
            ("TellRight", "▶", "Lights.BlinkerRightOn", Ok)
        };
        for (int i = 0; i < tells.Length; i++)
        {
            var t = tells[i];
            int x = pad + i * (w + gap);
            yield return Rect(t.Name + "Box", x, y, w, 52, Bg, radius: 10, border: (Edge, 1, 1, 1, 1));
            yield return Label(t.Name, t.Text, x, y, w, 52, t.Text.Length == 1 ? 26 : 16, Off, bold: true, align: 1,
                color: $"if({Raw(t.Prop)}, '{t.On}', '{Off}')");
        }
        yield return Label("FuelWarning", "LOW FUEL", pad, 608, col, 24, 15, Bad, bold: true, align: 1, visible: FuelLight);

        // Where the panel comes from (visible under the web view only if it can't load).
        yield return Label("PanelHint",
            $"Right: TruckingAdvisor's panel (localhost:{port}). Switch it on in TruckingAdvisor: More > Twitch, Overlay, SimHub & Convoy.",
            pad, 648, col, 52, 11, "#FF64748B", wrap: true);

        // TruckingAdvisor's panel in SimHub's web view.
        yield return Label("PanelOffline", "Start TruckingAdvisor and switch on its SimHub panel", GaugeColumnWidth + 40, Height / 2 - 40,
            Width - GaugeColumnWidth - 80, 80, 20, Muted, align: 1, wrap: true);
        yield return WebPage("TruckingAdvisorPanel", GaugeColumnWidth, 0, Width - GaugeColumnWidth, Height, PanelUrl(port));
    }

    private static string CruiseText => $"if({CruiseOn}, 'CRUISE ' + format({InLocalUnit(CruiseKmh)}, '0'), 'CRUISE OFF')";

    private static IEnumerable<JsonObject> LimitSign(string prefix, int left, int top, int width, int height)
    {
        yield return Rect(prefix + "Sign", left, top, width, height, "#FFFFFFFF", radius: 10, border: ("#FF111111", 4, 4, 4, 4), visible: HasLimit);
        yield return Label(prefix + "Caption", "SPEED\nLIMIT", left, top + height * 8 / 100, width, height * 33 / 100, height * 11 / 100,
            "#FF111111", bold: true, align: 1, wrap: true, visible: HasLimit);
        yield return Label(prefix + "Value", "65", left, top + height * 38 / 100, width, height * 55 / 100, height * 42 / 100,
            "#FF111111", bold: true, align: 1, text: $"format({InLocalUnit(LimitKmh)}, '0')", visible: HasLimit);
    }

    // ---- Triple-screen set: round, 4", round ----

    /// <summary>The disc of a round screen, a full-width ring track and its value arc (open at the bottom).</summary>
    /// <param name="percent">The ring's value, 0 to 100.</param>
    private static IEnumerable<JsonObject> RoundFace(string prefix, string percent, string color)
    {
        const int s = RoundSize;
        yield return Rect(prefix + "Disc", 0, 0, s, s, Bg, radius: s / 2);
        yield return Arc(prefix + "Ring", 30, 30, s - 60, s - 60, 42, percent, color);
    }

    private static IEnumerable<JsonObject> LeftItems(int port)
    {
        // The ring's full sweep is 80 mph or 130 km/h.
        string speedPercent = $"{SpeedLocal} * 100 / if({IsMph}, 80, 130)";
        foreach (var i in RoundFace("Speed", speedPercent, $"if({SpeedBad}, '{Bad}', '{Accent}')")) yield return i;

        // Driver score from TruckingAdvisor, small, at the top.
        yield return Label("ScoreCaption", "DRIVER SCORE", 250, 128, 300, 26, 20, Muted, bold: true, align: 1,
            visible: Ta(port, "d.score != '' && d.score != '--'", "false"));
        yield return Label("Score", "--", 250, 152, 300, 60, 50, Ok, bold: true, align: 1,
            text: Ta(port, "d.score", "''"),
            color: Ta(port, $"d.scoreValue >= 85 ? '{Ok}' : d.scoreValue >= 65 ? '{Accent}' : '{Bad}'", $"'{Muted}'"));

        // Speed and unit.
        yield return Label("Speed", "0", 150, 220, 500, 230, 210, Text, bold: true, align: 1,
            text: $"format({SpeedLocal}, '0')", color: $"if({SpeedBad}, '{Bad}', '{Text}')");
        yield return Label("SpeedUnit", "MPH", 250, 440, 300, 40, 32, Muted, bold: true, align: 1, text: $"if({IsMph}, 'MPH', 'KM/H')");

        foreach (var i in LimitSign("Limit", 335, 492, 130, 150)) yield return i;

        yield return Label("Cruise", "CRUISE OFF", 230, 660, 340, 56, 34, Dim, bold: true, align: 1,
            text: CruiseText, color: $"if({CruiseOn}, '{Ok}', '{Dim}')");

        yield return Label("BlinkLeft", "◀", 100, 360, 90, 90, 76, Off, bold: true, align: 1,
            color: $"if({Raw("Lights.BlinkerLeftOn")}, '{Ok}', '{Off}')");
        yield return Label("GameState", "", 250, 92, 300, 30, 20, Dim, bold: true, align: 1,
            text: $"if({GameRunning}, '', 'WAITING FOR GAME')");
    }

    private static IEnumerable<JsonObject> RightItems(int port)
    {
        const int s = RoundSize;
        foreach (var i in RoundFace("Rpm", RpmPercent, $"if({RpmPercent} >= 92, '{Bad}', '{Accent}')")) yield return i;
        // Inner fuel ring.
        yield return Arc("FuelRing", 104, 104, s - 208, s - 208, 16, Fuel, $"if({Fuel} <= 15, '{Bad}', '{Ok}')");

        // CB channel from TruckingAdvisor at the top, lit while someone is on the air.
        yield return Rect("ChannelBox", 320, 140, 160, 62, LcdBg, radius: 10, visible: Ta(port, "true", "false"));
        yield return Label("Channel", "CH 19", 320, 140, 160, 62, 38, Lcd, bold: true, align: 1,
            text: Ta(port, "'CH ' + d.ch", "''"));
        yield return Label("RadioState", "", 250, 206, 300, 30, 20, Muted, bold: true, align: 1,
            text: Ta(port, "d.radio", "''"),
            color: Ta(port, $"d.radioMode == 'tx' ? '{Bad}' : d.radioMode == 'rx' ? '{Ok}' : '{Muted}'", $"'{Muted}'"));

        yield return Label("Gear", "N", 200, 236, 400, 240, 220, Text, bold: true, align: 1, text: Gear);
        yield return Label("RpmValue", "0 RPM", 250, 470, 300, 44, 34, Muted, bold: true, align: 1, text: $"format({Rpm}, '0') + ' RPM'");
        yield return Label("FuelValue", "FUEL 0%", 250, 518, 300, 40, 30, Ok, bold: true, align: 1,
            text: $"'FUEL ' + format({Fuel}, '0') + '%'", color: $"if({Fuel} <= 15, '{Bad}', '{Ok}')");

        // Telltales in the gap at the bottom of the ring.
        yield return Rect("ParkBox", 250, 580, 140, 56, Bg, radius: 10, border: (Edge, 2, 2, 2, 2), fill: $"if({ParkBrake}, '#FF3A1212', '{Bg}')");
        yield return Label("Park", "PARK", 250, 580, 140, 56, 26, Off, bold: true, align: 1, color: $"if({ParkBrake}, '{Bad}', '{Off}')");
        string air = Raw("Axilliary.AirPressureWarning");
        yield return Rect("AirBox", 410, 580, 140, 56, Bg, radius: 10, border: (Edge, 2, 2, 2, 2), fill: $"if({air}, '#FF3A1212', '{Bg}')");
        yield return Label("Air", "AIR", 410, 580, 140, 56, 26, Off, bold: true, align: 1, color: $"if({air}, '{Bad}', '{Off}')");
        yield return Label("LowFuel", "LOW FUEL", 250, 652, 300, 40, 28, Bad, bold: true, align: 1, visible: FuelLight);

        yield return Label("BlinkRight", "▶", 610, 360, 90, 90, 76, Off, bold: true, align: 1,
            color: $"if({Raw("Lights.BlinkerRightOn")}, '{Ok}', '{Off}')");
    }

    private static IEnumerable<JsonObject> CenterItems(int port)
    {
        const int w = 800, h = 480, pad = 18;

        // CB radio: channel, its name, state, and the last line heard.
        yield return Rect("ChannelBox", pad, 14, 150, 60, LcdBg, radius: 10);
        yield return Label("Channel", "CH 19", pad, 14, 150, 60, 36, Lcd, bold: true, align: 1, text: Ta(port, "'CH ' + d.ch", "'CH --'"));
        yield return Label("ChannelName", "HIGHWAY TRAFFIC", 184, 14, 420, 32, 24, Text, bold: true,
            text: Ta(port, "d.chName", "'TRUCKINGADVISOR'"));
        yield return Label("Clock", "", 184, 46, 420, 26, 18, Muted, text: Ta(port, "d.time + (d.location ? '  ·  ' + d.location : '')", "''"));
        yield return Rect("RadioStateBox", 620, 22, 162, 44, Track, radius: 22,
            fill: Ta(port, "d.radioMode == 'tx' ? '#FF3A1212' : d.radioMode == 'rx' ? '#FF0F2A1A' : '#FF1E293B'", "'#FF1E293B'"));
        yield return Label("RadioState", "READY", 620, 22, 162, 44, 20, Muted, bold: true, align: 1, text: Ta(port, "d.radio", "''"),
            color: Ta(port, $"d.radioMode == 'tx' ? '{Bad}' : d.radioMode == 'rx' ? '{Ok}' : '{Muted}'", $"'{Muted}'"));

        yield return Rect("LineBox", pad, 86, w - 2 * pad, 112, Card, radius: 12, border: (Edge, 1, 1, 1, 1));
        yield return Label("Who", "", pad + 16, 92, w - 2 * pad - 32, 32, 22, Accent, bold: true,
            text: Ta(port, "d.who ? d.who : 'Nothing on the radio yet'", "''"),
            color: Ta(port, $"d.dispatch ? '{Info}' : d.mine ? '{Accent}' : '{Text}'", $"'{Text}'"));
        yield return Label("Line", "", pad + 16, 124, w - 2 * pad - 32, 70, 22, "#FFE2E8F0", wrap: true,
            text: Ta(port, "d.line.length > 120 ? d.line.substring(0, 117) + '...' : d.line", "''"));

        // Driver score.
        yield return Rect("ScoreBox", pad, 212, 230, 250, Card, radius: 12, border: (Edge, 1, 1, 1, 1));
        yield return Label("ScoreCaption", "DRIVER SCORE", pad, 222, 230, 26, 16, Muted, bold: true, align: 1);
        yield return Label("Score", "--", pad, 250, 230, 120, 100, Text, bold: true, align: 1, text: Ta(port, "d.score", "'--'"),
            color: Ta(port, $"d.scoreValue >= 85 ? '{Ok}' : d.scoreValue >= 65 ? '{Accent}' : d.scoreValue > 0 ? '{Bad}' : '{Muted}'", $"'{Muted}'"));
        yield return Label("Rating", "", pad + 10, 376, 210, 76, 18, Muted, bold: true, align: 1, wrap: true, text: Ta(port, "d.rating", "''"));

        // Delivery.
        const int dx = 264, dw = w - dx - pad;
        yield return Rect("DeliveryBox", dx, 212, dw, 170, Card, radius: 12, border: (Edge, 1, 1, 1, 1));
        yield return Label("DeliveryCaption", "DELIVERY", dx + 16, 222, 200, 24, 16, Muted, bold: true);
        yield return Label("Cargo", "No active delivery", dx + 16, 246, dw - 32, 34, 26, Text, bold: true,
            text: Ta(port, "d.active ? d.cargo : 'No active delivery'", "'No active delivery'"));
        yield return Label("Destination", "", dx + 16, 282, dw - 32, 30, 22, Muted, text: Ta(port, "d.dest ? 'to ' + d.dest : ''", "''"));
        yield return Gauge("Progress", dx + 16, 320, dw - 32, 16, value: Ta(port, "d.active ? d.progress : 0", "0"), color: Accent);
        yield return Label("Remaining", "", dx + 16, 342, dw - 32, 30, 22, Text,
            text: Ta(port, "d.left ? d.left + (d.eta ? '  ·  ETA ' + d.eta : '') : ''", "''"));

        // Warning (a grade ahead, or the last coaching call); offline notice.
        string hasAlert = Ta(port, "d.alert != ''", "false");
        yield return Rect("AlertBox", dx, 392, dw, 70, "#FF3A1212", radius: 12, visible: hasAlert,
            fill: Ta(port, "d.alertLevel == 'bad' ? '#FF7F1D1D' : '#FF78350F'", "'#FF3A1212'"));
        yield return Label("Alert", "", dx + 14, 392, dw - 28, 70, 20, Text, bold: true, wrap: true, visible: hasAlert, text: Ta(port, "d.alert", "''"));

        string offline = Ta(port, "false", "true");
        yield return Rect("OfflineBox", 0, 0, w, h, Bg, visible: offline);
        yield return Label("Offline", "", 60, 150, w - 120, 180, 26, Muted, align: 1, wrap: true, visible: offline,
            text: $"'Waiting for TruckingAdvisor (localhost:{port}). Start it and switch on More > Twitch, Overlay, SimHub & Convoy > Serve the SimHub panel.'");
    }

    // ---- Item builders ----

    private static JsonObject Label(string name, string sample, int left, int top, int width, int height, double size, string colorHex,
        bool bold = false, int align = 0, bool wrap = false, string? text = null, string? color = null, string? visible = null)
    {
        var o = new JsonObject
        {
            ["$type"] = TextType,
            ["IsTextItem"] = true,
            ["Font"] = "Segoe UI",
            ["FontWeight"] = bold ? "Bold" : "Normal",
            ["FontStyle"] = "Normal",
            ["FontSize"] = size,
            ["Text"] = sample,
            ["TextColor"] = colorHex,
            ["HorizontalAlignment"] = align,
            ["VerticalAlignment"] = 1,
            ["TextWrapping"] = wrap ? "Wrap" : "NoWrap"
        };
        Drawable(o, name, left, top, width, height, Clear, new JsonObject(),
            Bindings(("Text", text), ("TextColor", color), ("Visible", visible)));
        return o;
    }

    private static JsonObject Rect(string name, int left, int top, int width, int height, string fillHex, int radius = 0,
        (string Color, int L, int T, int R, int B)? border = null, string? fill = null, string? visible = null)
    {
        var o = new JsonObject { ["$type"] = RectType, ["IsRectangleItem"] = true };
        var b = new JsonObject();
        if (border is { } e)
        {
            b["BorderColor"] = e.Color;
            if (e.T != 0) b["BorderTop"] = e.T;
            if (e.B != 0) b["BorderBottom"] = e.B;
            if (e.L != 0) b["BorderLeft"] = e.L;
            if (e.R != 0) b["BorderRight"] = e.R;
        }
        if (radius != 0)
            foreach (var corner in new[] { "RadiusTopLeft", "RadiusTopRight", "RadiusBottomLeft", "RadiusBottomRight" }) b[corner] = radius;
        Drawable(o, name, left, top, width, height, fillHex, b, Bindings(("BackgroundColor", fill), ("Visible", visible)));
        return o;
    }

    private static JsonObject Gauge(string name, int left, int top, int width, int height, string value, string color)
    {
        var o = new JsonObject
        {
            ["$type"] = GaugeType,
            ["IsLinearGauge"] = true,
            ["GaugeOrientation"] = 0,
            ["GaugeAlignment"] = 0,
            ["AutoSize"] = false,
            ["GaugeColor"] = color.StartsWith('#') ? color : Accent,
            ["AlternateGaugeColor"] = Accent,
            ["UseAlternateStyle"] = false,
            ["Minimum"] = 0,
            ["Maximum"] = 100,
            ["Value"] = 0,
            ["Steps"] = 0
        };
        var border = new JsonObject();
        foreach (var corner in new[] { "RadiusTopLeft", "RadiusTopRight", "RadiusBottomLeft", "RadiusBottomRight" }) border[corner] = height / 2;
        Drawable(o, name, left, top, width, height, Track, border,
            Bindings(("Value", value), ("GaugeColor", color.StartsWith('#') ? null : color)));
        return o;
    }

    /// <summary>A 270° ring gauge (0 to 100), open at the bottom (SimHub's ArcGaugeItem; angles in degrees, 0 = top).</summary>
    private static JsonObject Arc(string name, int left, int top, int width, int height, double thickness, string percent, string color)
    {
        var o = new JsonObject
        {
            ["$type"] = ArcType,
            ["ArcThickness"] = thickness,
            ["UseEllipse"] = false,
            ["SweepMinValue"] = 0.0,
            ["SweepMaxValue"] = 100.0,
            ["Value"] = 0.0,
            ["SweepStartAngle"] = -135.0,
            ["SweepAngle"] = 270.0,
            ["SweepAngleCounterClockWise"] = false,
            ["SweepRangeSteps"] = 0.0,
            ["SweepBackgroundColor"] = Track,
            ["SweepColor"] = Accent,
            ["CompleteCircleEnabled"] = false
        };
        Drawable(o, name, left, top, width, height, Clear, border: null,
            Bindings(("Value", $"min(max({percent}, 0), 100)"), ("SweepColor", color)));
        return o;
    }

    private static JsonObject WebPage(string name, int left, int top, int width, int height, string url)
    {
        var o = new JsonObject
        {
            ["$type"] = WebType,
            ["StartAddress"] = url,
            ["AllowTransparency"] = false,
            ["ClickThrough"] = false
        };
        Drawable(o, name, left, top, width, height, Bg, border: null, bindings: null);
        return o;
    }

    /// <summary>The keys every drawable item shares, in Dash Studio's order.</summary>
    private static void Drawable(JsonObject o, string name, int left, int top, int width, int height, string background,
        JsonObject? border, JsonObject? bindings)
    {
        o["Left"] = left;
        o["Top"] = top;
        o["Width"] = width;
        o["Height"] = height;
        o["Visible"] = true;
        o["BackgroundColor"] = background;
        if (border is not null) o["BorderStyle"] = border;
        if (bindings is not null) o["Bindings"] = bindings;
        o["Id"] = string.Empty; // set per dashboard in BuildDashboardJson
        o["Name"] = name;
        o["RenderingSkip"] = 0;
        // Items that read TruckingAdvisor's data needn't redraw every frame: it changes a few times a second at most.
        bool readsApp = bindings is not null && bindings.ToJsonString().Contains("downloadstringasync", StringComparison.Ordinal);
        o["MinimumRefreshIntervalMS"] = readsApp ? 200 : 0;
    }

    /// <summary>
    /// Formula bindings (mode 2) for the targets that have one; null when none do. An expression starting with
    /// "js:" is JavaScript (SimHub's Jint engine), anything else NCalc.
    /// </summary>
    private static JsonObject? Bindings(params (string Target, string? Expression)[] targets)
    {
        var o = new JsonObject();
        foreach (var (target, expr) in targets)
        {
            if (string.IsNullOrEmpty(expr)) continue;
            var formula = new JsonObject();
            if (expr.StartsWith("js:", StringComparison.Ordinal))
            {
                formula["Interpreter"] = 1;
                formula["JSExt"] = 0;
                formula["Expression"] = expr[3..];
            }
            else formula["Expression"] = expr;
            o[target] = new JsonObject { ["Formula"] = formula, ["Mode"] = 2 };
        }
        return o.Count == 0 ? null : o;
    }

    /// <summary>A GUID that is always the same for the same name (so re-exporting gives an identical dashboard).</summary>
    internal static string StableGuid(string name)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes("TruckingAdvisor.SimHub/" + name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // version 3 (name-based, MD5)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash).ToString();
    }
}
