using System.Text.RegularExpressions;
using TruckingAdvisor.Shared;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.AI.Streaming;

/// <summary>
/// A compact picture of one truck, small enough to send between convoy PCs. Built from local telemetry or
/// received from another driver. Values that aren't known are null and never spoken.
/// </summary>
public sealed record TruckFacts
{
    public string Driver { get; init; } = string.Empty;
    public double? SpeedMph { get; init; }
    public double? LimitMph { get; init; }
    public double? FuelPercent { get; init; }
    public double? FuelGallons { get; init; }
    public double? RangeMiles { get; init; }
    public double? Mpg { get; init; }
    public double? EngineWear { get; init; }
    public double? TireWear { get; init; }
    public double? ChassisWear { get; init; }
    public double? CargoDamage { get; init; }
    public string? Cargo { get; init; }
    public double? CargoTons { get; init; }
    public string? Destination { get; init; }
    public double? EtaMinutes { get; init; }
    public double? MilesLeft { get; init; }
    /// <summary>Game minutes until the driver must rest.</summary>
    public int? RestDueMinutes { get; init; }
    public string? Place { get; init; }
    public string? Highway { get; init; }
    public bool? Night { get; init; }
    public string? Truck { get; init; }

    public bool HasData => SpeedMph is not null;

    public static TruckFacts From(AtsTelemetrySnapshot t, string driver)
    {
        bool live = t.IsLive || t.IsDemoData;
        if (!live) return new TruckFacts { Driver = driver };
        static double? Pos(double v) => v > 0 ? Math.Round(v, 1) : null;
        static string? Txt(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        return new TruckFacts
        {
            Driver = driver,
            SpeedMph = Math.Round(t.SpeedMph),
            LimitMph = Pos(t.SpeedLimitMph),
            FuelPercent = t.HasFuelCapacity ? Math.Round(t.FuelPercent) : null,
            FuelGallons = t.HasFuelCapacity ? Math.Round(t.FuelGallons) : null,
            RangeMiles = Pos(Math.Round(t.EstimatedRangeMiles)),
            Mpg = t.HasFuelEconomy ? Math.Round(t.AverageMpg, 1) : null,
            EngineWear = Math.Round(t.DamageEngine * 100),
            TireWear = Math.Round(t.DamageWheels * 100),
            ChassisWear = Math.Round(t.DamageChassis * 100),
            CargoDamage = t.JobActive ? Math.Round(t.DamageCargo * 100) : null,
            Cargo = t.JobActive ? Txt(t.CargoName) : null,
            CargoTons = t.JobActive && t.CargoWeightLbs > 0 ? Math.Round(t.CargoWeightLbs / 2000.0) : null,
            Destination = t.JobActive ? Txt(t.DestinationCity) : null,
            EtaMinutes = t.JobActive ? Pos(Math.Round(t.NavigationTimeRemainingMinutes)) : null,
            MilesLeft = t.JobActive ? Pos(Math.Round(t.NavigationDistanceRemainingMiles)) : null,
            RestDueMinutes = t.HasRestData ? t.NextRestStopGameMinutes : null,
            Place = Txt(t.RealWorldPlace) ?? Txt(t.CurrentCity),
            Highway = Txt(t.ProbableHighway),
            Night = t.GameTimeMinutes > 0 ? t.IsNightDriving : null,
            Truck = Txt($"{t.TruckMake} {t.TruckModel}")
        };
    }

    /// <summary>One line for an AI prompt ("Zay: 62 mph near Reno, fuel 40%, hauling lumber to Boise").</summary>
    public string Describe()
    {
        if (!HasData) return $"{Driver}: no game data";
        var parts = new List<string> { $"{Units.Speed(SpeedMph ?? 0):F0} {Units.SpeedUnit}" };
        if (Place is not null) parts.Add($"near {Place}");
        if (Highway is not null) parts.Add($"on {Highway}");
        if (FuelPercent is { } f) parts.Add($"fuel {f:F0}%");
        if (Cargo is not null) parts.Add($"hauling {Cargo}{(Destination is null ? "" : $" to {Destination}")}");
        if (EngineWear is { } e && e >= 10) parts.Add($"engine wear {e:F0}%");
        return $"{Driver}: {string.Join(", ", parts)}";
    }
}

/// <summary>
/// Answers radio callers with no AI model at all: a keyword intent match picks a topic (fuel, wear, speed,
/// load, rest, location) and fills a short CB line with real telemetry. Works on every PC out of the box;
/// when an AI model is set up, the radio uses that instead and only falls back to this.
/// </summary>
public static class CbTemplateResponder
{
    private enum Intent { Fuel, Wear, Speed, Load, Rest, Where, Greeting, Signoff, General }

    private static readonly (Intent Intent, Regex Pattern)[] Intents =
    {
        (Intent.Fuel, new Regex(@"\b(fuel|gas|diesel|tank|tanks|range|mpg|empty|refuel)\b", RegexOptions.IgnoreCase)),
        (Intent.Wear, new Regex(@"\b(damage|damaged|wear|tires?|tyres?|engine|motor|transmission|chassis|broke|broken|repair|shop|health|condition|brakes?)\b", RegexOptions.IgnoreCase)),
        (Intent.Speed, new Regex(@"\b(bear|bears|smokey|smokeys|cops?|police|speed|speeding|fast|slow|scales?|weigh|ticket|radar|limit|mph|kmh)\b", RegexOptions.IgnoreCase)),
        (Intent.Load, new Regex(@"\b(eta|destination|hauling|haul|cargo|load|freight|trailer|deliver|delivery|late|on time|pay|money)\b", RegexOptions.IgnoreCase)),
        (Intent.Rest, new Regex(@"\b(hos|sleep|sleepy|tired|fatigue|hours|break|rest|logbook|nap)\b", RegexOptions.IgnoreCase)),
        (Intent.Where, new Regex(@"\b(where|location|heading|city|town|highway|road|route|state)\b", RegexOptions.IgnoreCase)),
        (Intent.Signoff, new Regex(@"\b(bye|later|good ?night|stay safe|clear|signing off|take care)\b", RegexOptions.IgnoreCase)),
        (Intent.Greeting, new Regex(@"\b(hi|hello|hey|howdy|yo|breaker|anybody|anyone|copy|radio check|shout ?out|good morning|evening)\b", RegexOptions.IgnoreCase)),
    };

    /// <summary>
    /// A reply to <paramref name="callerName"/> from <paramref name="responderHandle"/> in the given role,
    /// grounded in <paramref name="facts"/> (the truck being asked about). <paramref name="aboutCaller"/>: the
    /// facts are the caller's own truck (a convoy driver), so the reply says "you".
    /// </summary>
    public static string Reply(CallerRole role, string responderHandle, string callerName, string message, TruckFacts facts, Random random, bool aboutCaller = false)
    {
        var intent = Classify(message);
        string who = callerName;
        var driver = aboutCaller ? new Subject("you", "you're", "you've got") : Subject.Of(string.IsNullOrWhiteSpace(facts.Driver) ? "the driver" : facts.Driver);
        string Pick(params string[] options) => options[random.Next(options.Length)];

        string lead = role switch
        {
            CallerRole.Dispatch => Pick($"Dispatch copies, {who}.", $"Dispatch here, {who}.", $"Go ahead, {who}, this is dispatch."),
            CallerRole.Mechanic => Pick($"Shop here, {who}.", $"Mechanic copies, {who}.", $"{who}, this is the shop."),
            CallerRole.Safety => Pick($"Safety copies, {who}.", $"Safety desk here, {who}.", $"Go ahead, {who}, safety here."),
            _ => Pick($"Ten-four, {who}.", $"Copy that, {who}.", $"I hear you, {who}.", $"Yeah, {who}, I got you.")
        };

        if (intent == Intent.Signoff)
            return $"{Pick($"Ten-four, {who}.", $"Roger that, {who}.")} {Pick("Keep the shiny side up.", "Catch you on the flip-flop.", "Drive safe out there.")} {responderHandle} clear.";

        if (!facts.HasData)
            return $"{lead} {Pick("We're off the road right now, so I've got no numbers for you.", "No game data coming through at the moment, but I copy you loud and clear.", "Truck's parked with the gauges dark - holler again once we're rolling.")}";

        // Roles nudge vague questions toward their own speciality.
        if (intent is Intent.General or Intent.Greeting)
        {
            intent = role switch
            {
                CallerRole.Mechanic => Intent.Wear,
                CallerRole.Dispatch => Intent.Load,
                CallerRole.Safety => Intent.Speed,
                _ => intent
            };
        }

        string body = intent switch
        {
            Intent.Fuel => FuelLine(facts, driver, random),
            Intent.Wear => WearLine(facts, random),
            Intent.Speed => SpeedLine(facts, driver, random),
            Intent.Load => LoadLine(facts, driver, random),
            Intent.Rest => RestLine(facts, driver, random),
            Intent.Where => WhereLine(facts, driver, random),
            Intent.Greeting => GreetingLine(facts, responderHandle, random),
            _ => GeneralLine(facts, random)
        };
        return $"{lead} {body}";
    }

    private static Intent Classify(string message)
    {
        foreach (var (intent, pattern) in Intents)
            if (pattern.IsMatch(message)) return intent;
        return Intent.General;
    }

    private static string Speed(double mph) => $"{Units.Speed(mph):F0}";
    private static string Dist(double miles) => $"{Units.Dist(miles):F0} {Units.DistWord}";

    /// <summary>Who the line is about, with the verb forms that go with it ("Zay is" / "you're").</summary>
    private sealed record Subject(string Name, string Is, string Has)
    {
        public static Subject Of(string name) => new(name, $"{name} is", $"{name} has");
    }

    private static string FuelLine(TruckFacts f, Subject driver, Random r)
    {
        if (f.FuelPercent is not { } pct) return "Fuel gauge isn't reading on this rig, so I can't say.";
        string range = f.RangeMiles is { } rm ? $", good for about {Dist(rm)}" : string.Empty;
        string verdict = pct < 15 ? " Better find a fuel island real soon." : pct < 30 ? " Might want to plan a fuel stop." : " Plenty in the tanks.";
        return r.Next(2) == 0
            ? $"{Cap(driver.Is)} sitting on {pct:F0} percent fuel{range}.{verdict}"
            : $"Tanks read {pct:F0} percent{range}.{verdict}";
    }

    private static string WearLine(TruckFacts f, Random r)
    {
        double engine = f.EngineWear ?? 0, tires = f.TireWear ?? 0, chassis = f.ChassisWear ?? 0;
        double worst = Math.Max(engine, Math.Max(tires, chassis));
        string verdict = worst < 5 ? "She's running clean, no shop stop needed."
            : worst < 20 ? "A little worn, nothing to worry about yet."
            : "We'll want a service bay after this drop.";
        string cargo = f.CargoDamage is { } cd && cd >= 1 ? $" Cargo's taken {cd:F0} percent damage." : string.Empty;
        return r.Next(2) == 0
            ? $"Engine wear is {engine:F0} percent, tires {tires:F0}, frame {chassis:F0}. {verdict}{cargo}"
            : $"Motor's at {engine:F0} percent wear and the tires at {tires:F0}. {verdict}{cargo}";
    }

    private static string SpeedLine(TruckFacts f, Subject driver, Random r)
    {
        double speed = f.SpeedMph ?? 0;
        if (speed < 3) return r.Next(2) == 0 ? "We're sitting still right now, so the bears can't touch us." : "Parked up, nothing on the radar.";
        if (f.LimitMph is { } limit)
        {
            string verdict = speed > limit + 5 ? "better back out of it before a bear spots us!"
                : speed > limit + 1 ? "a hair over, easing off now."
                : "right on the legal limit.";
            return $"{Cap(driver.Is)} rolling {Speed(speed)} in a {Speed(limit)} - {verdict}";
        }
        return r.Next(2) == 0
            ? $"Running {Speed(speed)} {Units.SpeedSpoken}, eyes peeled for smokeys."
            : $"Holding {Speed(speed)}, no bears in sight so far.";
    }

    private static string LoadLine(TruckFacts f, Subject driver, Random r)
    {
        if (f.Cargo is null) return r.Next(2) == 0 ? "We're bobtailing between loads right now." : "No load on at the moment, looking for the next one.";
        string weight = f.CargoTons is { } t ? $" ({t:F0} tons)" : string.Empty;
        string dest = f.Destination is { } d ? $" bound for {d}" : string.Empty;
        string eta = f.EtaMinutes is { } m ? $", about {FormatMinutes(m)} out" : f.MilesLeft is { } ml ? $", {Dist(ml)} to go" : string.Empty;
        return r.Next(2) == 0
            ? $"Hauling {f.Cargo}{weight}{dest}{eta}."
            : $"{Cap(driver.Has)} {f.Cargo}{weight} in the box{dest}{eta}.";
    }

    private static string RestLine(TruckFacts f, Subject driver, Random r)
    {
        if (f.RestDueMinutes is not { } rest) return "Logbook's not reporting, but we'll take a break when it's due.";
        if (rest <= 60) return $"Only {FormatMinutes(rest)} left before a mandatory rest - time to find a truck stop.";
        return r.Next(2) == 0
            ? $"{Cap(driver.Has)} {FormatMinutes(rest)} left on the clock before a rest is due."
            : $"Logbook shows {FormatMinutes(rest)} of driving before we need to shut down.";
    }

    private static string WhereLine(TruckFacts f, Subject driver, Random r)
    {
        string where = f.Place is { } p ? $"near {p}" : "out on the open road";
        string road = f.Highway is { } h ? $" on {h}" : string.Empty;
        string dest = f.Destination is { } d ? $", heading for {d}" : string.Empty;
        return r.Next(2) == 0 ? $"{(driver.Name == "you" ? "You're" : "We're")} {where}{road}{dest}." : $"{Cap(driver.Is)} rolling {where}{road}{dest}.";
    }

    private static string GreetingLine(TruckFacts f, string handle, Random r)
    {
        string speed = f.SpeedMph is { } s && s > 3 ? $"rolling {Speed(s)}" : "parked up";
        string extra = f.Cargo is { } c ? $" with {c} out back" : string.Empty;
        return r.Next(3) switch
        {
            0 => $"Loud and clear on the one-nine! {handle} here, {speed}{extra}.",
            1 => $"Appreciate you keying up! We're {speed}{extra}.",
            _ => $"Reading you five-by-five. {speed[..1].ToUpperInvariant()}{speed[1..]}{extra} and clean green lights ahead."
        };
    }

    private static string GeneralLine(TruckFacts f, Random r)
    {
        string speed = f.SpeedMph is { } s && s > 3 ? $"cruising at {Speed(s)}" : "parked for a spell";
        string dest = f.Destination is { } d ? $" on the way to {d}" : string.Empty;
        return r.Next(2) == 0 ? $"Can't say I know about that, but we're {speed}{dest}." : $"Good to hear from you. We're {speed}{dest}.";
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string FormatMinutes(double minutes)
    {
        int m = (int)Math.Round(minutes);
        if (m < 60) return $"{m} minutes";
        int h = m / 60, rest = m % 60;
        return rest == 0 ? $"{h} hour{(h == 1 ? "" : "s")}" : $"{h} hour{(h == 1 ? "" : "s")} {rest} minutes";
    }
}
