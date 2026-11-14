using System.Text.Json;
using System.Text.Json.Serialization;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.AI;

/// <summary>One line said on the radio, kept briefly for conversation continuity.</summary>
public sealed record RadioTurn(string Handle, string Text, int Channel = 19);

/// <summary>
/// Builds the small, structured context sent to the AI. Unknown values are left out entirely
/// (never filled with guesses), so the model can't repeat made-up numbers.
/// </summary>
public static class ContextBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static string Lower(string s) => string.IsNullOrEmpty(s) ? "on the road" : char.ToLowerInvariant(s[0]) + s[1..];

    private static string? Text(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static double? Positive(float v) => v > 0 ? Math.Round(v) : null;

    /// <summary>Rounds to the nearest 1,000 (Math.Round can't take negative digits).</summary>
    private static double? NearestThousand(float v) => v > 0 ? Math.Round(v / 1000.0) * 1000 : null;

    public static object BuildTelemetryContext(VirtualCompanyProfile company, AtsTelemetrySnapshot t)
    {
        bool live = t.IsLive || t.IsDemoData;
        return new
        {
            player = new
            {
                cbHandle = Text(company.PlayerHandle),
                truck = Text($"{t.TruckMake} {t.TruckModel}".Trim())
            },
            dataSource = t.IsDemoData ? "demo" : live ? "live game telemetry" : "no game connection",
            telemetry = !live ? null : new
            {
                speedMph = Math.Round(t.SpeedMph),
                speedLimitMph = Positive(t.SpeedLimitMph),
                fuelPercent = t.HasFuelCapacity ? Math.Round(t.FuelPercent) : (double?)null,
                estimatedRangeMiles = Positive(t.EstimatedRangeMiles),
                // Estimated by matching the game map to real geography using cities the player has delivered to.
                // Approximate: the game map is a scaled-down version of the real one.
                approximateLocation = t.RealWorldPlace.Length > 0 ? $"{t.RealWorldPlace} (estimated)"
                    : string.IsNullOrEmpty(t.CurrentCity) ? null
                    : $"near {t.CurrentCity}{(string.IsNullOrEmpty(t.CurrentState) ? "" : ", " + t.CurrentState)} (approximate)",
                probableHighway = Text(t.ProbableHighway),
                nextTownAhead = Text(t.NextTownAhead),
                gameTime = t.GameTimeMinutes > 0 ? t.FormattedGameTime : null,
                nightDriving = t.GameTimeMinutes > 0 ? t.IsNightDriving : (bool?)null,
                gradePercent = Math.Abs(t.CalculatedGradePercent) >= 2 ? Math.Round(t.CalculatedGradePercent, 1) : (double?)null,
                restDueInGameMinutes = t.HasRestData ? t.NextRestStopGameMinutes : (int?)null
            },
            delivery = !live || !t.JobActive ? null : new
            {
                cargo = Text(t.CargoName),
                origin = Text(t.OriginCity),
                destination = Text(t.DestinationCity),
                remainingMiles = Positive(t.NavigationDistanceRemainingMiles),
                cargoWeightLbs = NearestThousand(t.CargoWeightLbs)
            }
        };
    }

    /// <summary>Reply length guidance for each conversation mode.</summary>
    public static string LengthRule(ConversationTimingMode mode) => mode switch
    {
        ConversationTimingMode.Brief => "ONE short CB transmission: a single sentence, under 20 words",
        ConversationTimingMode.Chatty => "ONE CB transmission of 2-5 sentences, under 90 words; a short story or joke is fine when it fits",
        _ => "ONE short CB transmission (1-3 sentences, under 50 words)"
    };

    public static string BuildTruckerSystemPrompt(TruckerPersonality trucker, VirtualCompanyProfile company, AtsTelemetrySnapshot telemetry,
        IReadOnlyList<RadioTurn> recentTurns, int channel,
        ConversationTimingMode mode = ConversationTimingMode.Natural, IReadOnlyList<TruckerPersonality>? otherDrivers = null)
    {
        var context = new
        {
            channel,
            // Fictional radio-world gossip: what this trucker has heard about other simulated drivers.
            otherDriversYouKnow = otherDrivers?.Where(o => o.Id != trucker.Id).Take(3)
                .Select(o => $"{o.Handle} ({o.Truck}, from {o.HomeState}): last heard {Lower(o.CurrentFictionalStatus)}").ToArray(),
            situation = BuildTelemetryContext(company, telemetry),
            relationship = new
            {
                level = (int)trucker.Relationship,
                title = trucker.Relationship.ToString(),
                conversationsSoFar = trucker.ConversationCount,
                daysSinceLastTalk = trucker.LastSpokenAt is { } last ? Math.Round((DateTimeOffset.UtcNow - last).TotalDays, 1) : (double?)null,
                memories = trucker.MemoriesSnapshot().TakeLast(6).ToArray()
            },
            // Knowledge boundary: truckers never hear the private dispatch channel.
            recentRadio = recentTurns.Where(r => r.Channel != CBRadioService.DispatchChannel).TakeLast(6).Select(r => $"{r.Handle}: {r.Text}").ToArray()
        };

        string playerName = string.IsNullOrWhiteSpace(company.PlayerHandle) ? "(handle unknown - you may ask for it)" : company.PlayerHandle;
        return $"""
You are {trucker.FullName}, CB handle "{trucker.Handle}", a long-haul trucker from {trucker.HomeState} driving a {trucker.Truck}, {trucker.YearsExperience} years on the road.
Personality: {trucker.PersonalityTraits}. Speaking style: {trucker.SpeakingStyle}. {trucker.Background}
Favorite topics: {string.Join(", ", trucker.FavoriteTopics)}. Usual roads: {trucker.CurrentFictionalRegion}.
You are talking on CB radio with the player, handle {playerName}.

Rules:
1. Reply with {LengthRule(mode)}. Plain spoken words only: no stage directions, emojis or markdown.
2. Sound like a real trucker: natural, warm, unhurried. Use slang (ten-four, copy, driver) only where it fits, not in every line.
3. Only state facts about the player's truck, load, speed, fuel or location that appear in the JSON below. If something isn't there, you don't know it.
4. You are a simulated radio voice. Never claim to see the player's truck, nearby traffic, police, weather or road conditions; say you can't tell from where you are.
   If an approximate location, highway or next town is given, you may talk about that stretch the way truckers do
   (towns, truck stops, the scenery and what the road is generally like there), but treat it as "sounds like you're
   around...", never as fact, and don't invent specific businesses, exits or mile markers.
5. Stay consistent with your identity and with the memories listed. If this is your first conversation, act like it.
   You may mention the other drivers you know, but only what's listed; it's radio gossip, not something you saw.
6. Don't end every message with a question. Sign off naturally when the player says goodbye.
7. Never give advice that conflicts with driving safely.

Context JSON:
{JsonSerializer.Serialize(context, JsonOptions)}
""";
    }

    public static string BuildDispatcherSystemPrompt(VirtualCompanyProfile company, AtsTelemetrySnapshot telemetry, IReadOnlyList<RadioTurn> recentTurns)
    {
        var context = new
        {
            company = company.CompanyName,
            situation = BuildTelemetryContext(company, telemetry),
            // Knowledge boundary: dispatch only knows the company channel, not CB chatter.
            recentRadio = recentTurns.Where(r => r.Channel == CBRadioService.DispatchChannel).TakeLast(6).Select(r => $"{r.Handle}: {r.Text}").ToArray()
        };
        return $"""
You are {company.DispatcherName}, the dispatcher for {company.CompanyName}, talking to your driver on the private company channel. You know this driver from earlier calls listed below.
Be brief, professional and friendly (1-2 sentences, under 40 words). Plain spoken words only.
Use only facts from the JSON below; if ETA, fuel or damage data is missing, say you don't have it.
Remind about fuel or rest only when the data shows it is relevant. Never pressure the driver to speed or skip rest.

Context JSON:
{JsonSerializer.Serialize(context, JsonOptions)}
""";
    }

    public static string BuildBanterPrompt(TruckerPersonality a, TruckerPersonality b, VirtualCompanyProfile company, AtsTelemetrySnapshot telemetry, int lines)
    {
        var context = BuildTelemetryContext(company, telemetry);
        return $$"""
Write a short CB radio exchange of exactly {{lines}} lines between two simulated truckers, alternating speakers, starting with {{a.Handle}}.
{{a.Handle}}: {{a.FullName}}, {{a.Truck}}, from {{a.HomeState}}. {{a.PersonalityTraits}}. {{a.SpeakingStyle}}.
{{b.Handle}}: {{b.FullName}}, {{b.Truck}}, from {{b.HomeState}}. {{b.PersonalityTraits}}. {{b.SpeakingStyle}}.
Topics: road chatter, truck talk, freight, truck stops. No claims about real traffic, police or weather. Each line under 30 words.
They may mention the player's general situation only using facts in this JSON: {{JsonSerializer.Serialize(context, JsonOptions)}}
Return only JSON: {"lines":[{"handle":"...","text":"..."}]}
""";
    }
}
