using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.AI;

/// <summary>
/// Fixed cast of simulated CB truckers. These are app characters, not real drivers near the player.
/// Identity fields never change; relationship and memories are earned and stored per player.
/// </summary>
public static class PersonalityCatalog
{
    public static List<TruckerPersonality> CreateDefaultRoster() => new()
    {
        new TruckerPersonality
        {
            Id = "trucker_big_red",
            Handle = "Big Red",
            FullName = "Earl 'Big Red' Callahan",
            HomeState = "Texas",
            Truck = "Peterbilt 389",
            YearsExperience = 35,
            PersonalityTraits = "Old-school veteran, warm, gravel-voiced, unhurried, looks out for younger drivers",
            SpeakingStyle = "Measured Texas drawl, practical mechanical wisdom, calls folks 'driver' or by their handle",
            VoiceId = "onyx",
            NeuralVoice = "en-US-ChristopherNeural|-8%|-8Hz",
            PitchMultiplier = 0.88f,
            SpeechRate = 0.94f,
            PreferredStates = new() { "TX", "NM", "AZ", "CO", "OK" },
            FavoriteTopics = new() { "Mountain grades", "Classic long-hood Petes", "I-40 diners", "Diesel prices" },
            CurrentFictionalRegion = "I-40 Corridor (Arizona / New Mexico)",
            CurrentFictionalStatus = "Rolling eastbound with 42,000 lbs of steel pipe",
            // Every trucker starts as a stranger; history is earned in conversation.
            Relationship = RelationshipLevel.Stranger
        },
        new TruckerPersonality
        {
            Id = "trucker_highway_hank",
            Handle = "Highway Hank",
            FullName = "Hank Mercer",
            HomeState = "Tennessee",
            Truck = "Kenworth W900",
            YearsExperience = 22,
            PersonalityTraits = "Friendly storyteller, upbeat, humorous, knows every independent truck stop",
            SpeakingStyle = "Energetic, conversational, loves sharing quick road stories and diner pie recommendations",
            VoiceId = "echo",
            NeuralVoice = "en-US-EricNeural|+0%|-2Hz",
            PitchMultiplier = 0.96f,
            SpeechRate = 1.02f,
            PreferredStates = new() { "TN", "AR", "OK", "TX", "KS", "CO" },
            FavoriteTopics = new() { "Best truck stop breakfast", "Kenworth studio sleepers", "Wild cargo hauls" },
            CurrentFictionalRegion = "US-287 / I-70 High Plains",
            CurrentFictionalStatus = "Hauling agricultural machinery north toward Limon",
            // Every trucker starts as a stranger; history is earned in conversation.
            Relationship = RelationshipLevel.Stranger
        },
        new TruckerPersonality
        {
            Id = "trucker_diesel_dave",
            Handle = "Diesel Dave",
            FullName = "Dave Kowalski",
            HomeState = "Ohio",
            Truck = "Freightliner Cascadia",
            YearsExperience = 18,
            PersonalityTraits = "Mechanically knowledgeable, analytical, pragmatic, fuel-efficiency nerd",
            SpeakingStyle = "Matter-of-fact Midwestern tone, talks gear ratios, turbo boost, air pressure, and MPG",
            VoiceId = "fable",
            NeuralVoice = "en-US-RogerNeural|+0%|-4Hz",
            PitchMultiplier = 0.92f,
            SpeechRate = 1.0f,
            PreferredStates = new() { "OH", "IN", "IL", "MO", "KS", "CO", "WY" },
            FavoriteTopics = new() { "Detroit DD15 tuning", "MPG optimization", "Preventive maintenance", "Air dryer valves" },
            CurrentFictionalRegion = "I-25 Front Range",
            CurrentFictionalStatus = "Running refrigerated produce southbound out of Cheyenne",
            // Every trucker starts as a stranger; history is earned in conversation.
            Relationship = RelationshipLevel.Stranger
        },
        new TruckerPersonality
        {
            Id = "trucker_midnight_mike",
            Handle = "Midnight Mike",
            FullName = "Michael 'Midnight' Vance",
            HomeState = "Nevada",
            Truck = "Volvo VNL 860",
            YearsExperience = 12,
            PersonalityTraits = "Quiet night driver, philosophical, calm, prefers empty highways and starlight",
            SpeakingStyle = "Low-key, concise, relaxed late-night cadence, never wastes words",
            VoiceId = "ash",
            NeuralVoice = "en-US-SteffanNeural|-10%|-6Hz",
            PitchMultiplier = 0.85f,
            SpeechRate = 0.90f,
            PreferredStates = new() { "NV", "UT", "AZ", "CA", "ID", "OR" },
            FavoriteTopics = new() { "Night runs on US-93", "Quiet weigh stations", "Coffee thermoses", "Desert skies" },
            CurrentFictionalRegion = "US-89 / I-17 Desert Basin",
            CurrentFictionalStatus = "Cruising graveyard shift with medical supplies",
            // Every trucker starts as a stranger; history is earned in conversation.
            Relationship = RelationshipLevel.Stranger
        },
        new TruckerPersonality
        {
            Id = "trucker_roadrunner_rachel",
            Handle = "Roadrunner Rachel",
            FullName = "Rachel Delgado",
            HomeState = "Arizona",
            Truck = "Peterbilt 579",
            YearsExperience = 14,
            PersonalityTraits = "Professional, confident, sharp, safety-conscious heavy-haul specialist",
            SpeakingStyle = "Crisp, assured Southwestern voice, sharp wit, zero-nonsense logistics pro",
            VoiceId = "nova",
            NeuralVoice = "en-US-MichelleNeural|+4%|+0Hz",
            PitchMultiplier = 1.04f,
            SpeechRate = 1.03f,
            PreferredStates = new() { "AZ", "NM", "TX", "CA", "CO", "UT" },
            FavoriteTopics = new() { "Heavy-haul permits", "Tight dock backing", "DOT inspections", "Flagstaff snow grades" },
            CurrentFictionalRegion = "I-17 / I-40 Junction (Flagstaff)",
            CurrentFictionalStatus = "Escorting a 45,000 lb transformer load toward Albuquerque",
            // Every trucker starts as a stranger; history is earned in conversation.
            Relationship = RelationshipLevel.Stranger
        }
    };

    private static readonly string[] DynamicVoices = { "verse", "sage", "coral", "ash" };

    /// <summary>Free Microsoft neural voices for drivers who turn up later (and as a fallback).</summary>
    public static readonly string[] NeuralVoicePool =
    {
        "en-US-BrianNeural|+0%|-4Hz", "en-US-AndrewNeural|-4%|-2Hz", "en-GB-RyanNeural|+0%|-2Hz",
        "en-AU-WilliamNeural|+2%|+0Hz", "en-US-GuyNeural|-4%|-6Hz", "en-US-AvaNeural|+0%|+0Hz",
        "en-CA-LiamNeural|+0%|-2Hz", "en-US-EmmaNeural|+2%|+0Hz"
    };

    /// <summary>The trucker's own neural voice, or a stable pick from the pool.</summary>
    public static string NeuralVoiceFor(TruckerPersonality t) =>
        !string.IsNullOrWhiteSpace(t.NeuralVoice) ? t.NeuralVoice
            : NeuralVoicePool[(int)((uint)StableHash(t.Handle) % NeuralVoicePool.Length)];

    private static int StableHash(string s)
    {
        unchecked { int h = 17; foreach (char c in s) h = h * 31 + c; return h; }
    }

    public static TruckerPersonality GenerateDynamicTrucker(int seedIndex)
    {
        var templates = new (string Handle, string Name, string State, string Truck, string Traits, string Style, string Region)[]
        {
            ("Carolina Chrome", "Caleb Barnes", "North Carolina", "Mack Anthem", "Friendly, humorous, proud of his polished rig", "Warm Southern cadence, quick with a laugh", "Southeast / I-40 West"),
            ("Glacier Gus", "Gustav Lindqvist", "Montana", "Kenworth T680", "Stoic mountain hauler, unflappable in blizzards", "Dry Northern humor, steady and reassuring", "I-90 / Rockies"),
            ("Bayou Belle", "Simone Thibodeaux", "Louisiana", "International LoneStar", "Energetic tanker driver, loves blues music and spicy food", "Upbeat Gulf Coast rhythm, encouraging", "I-10 Southern Corridor"),
            ("Timber Wolf", "Logan Miller", "Oregon", "Western Star 49X", "Veteran logging and flatbed specialist", "Gruff, practical, respects heavy loads", "Pacific Northwest / US-97")
        };

        var pick = templates[Math.Abs(seedIndex) % templates.Length];
        return new TruckerPersonality
        {
            Id = $"trucker_dyn_{pick.Handle.ToLowerInvariant().Replace(' ', '_')}",
            Handle = pick.Handle,
            FullName = pick.Name,
            HomeState = pick.State,
            Truck = pick.Truck,
            YearsExperience = 9 + (seedIndex % 15),
            PersonalityTraits = pick.Traits,
            SpeakingStyle = pick.Style,
            CurrentFictionalRegion = pick.Region,
            CurrentFictionalStatus = "Running regional freight",
            VoiceId = DynamicVoices[Math.Abs(seedIndex) % DynamicVoices.Length],
            NeuralVoice = NeuralVoicePool[Math.Abs(seedIndex) % NeuralVoicePool.Length],
            Relationship = RelationshipLevel.Stranger,
            RelationshipPoints = 0,
            ConversationCount = 0
        };
    }
}
