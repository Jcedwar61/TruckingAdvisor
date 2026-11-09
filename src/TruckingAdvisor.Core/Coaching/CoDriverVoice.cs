using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.Core.Coaching;

/// <summary>
/// Words coaching alerts in the chosen onboard co-driver personality. Deterministic templates,
/// no AI. Critical safety warnings keep their plain wording in every personality, and Minimal only
/// speaks High/Critical alerts. The co-driver is the voice in the cab, so it never uses CB effects.
/// </summary>
public static class CoDriverVoice
{
    /// <summary>Text to speak, or null when this personality stays silent for the incident.</summary>
    public static string? Phrase(DrivingIncident incident, CoDriverPersonality personality)
    {
        if (incident.Severity == "Critical") return incident.AdvisoryMessage;
        if (personality == CoDriverPersonality.Minimal) return incident.Severity == "High" ? incident.AdvisoryMessage : null;

        float over = incident.Intensity;
        return (incident.IncidentType, personality) switch
        {
            ("Speeding", CoDriverPersonality.Professional) => $"You're {over:F0} over the limit. Please reduce speed.",
            ("Speeding", CoDriverPersonality.Friendly) => $"Hey, you're running about {over:F0} over. Let's ease it back a little.",
            ("Speeding", CoDriverPersonality.OldSchoolTrucker) => $"Easy on the hammer, driver. You're {over:F0} over. No sense feeding the bears.",
            ("Speeding", CoDriverPersonality.StrictInstructor) => $"Speed violation: {over:F0} miles per hour over the posted limit. Correct it now.",

            ("HarshBraking", CoDriverPersonality.Professional) => "Hard braking recorded. Increase your following distance.",
            ("HarshBraking", CoDriverPersonality.Friendly) => "Whoa, that was a hard stop. Give yourself a bit more room next time.",
            ("HarshBraking", CoDriverPersonality.OldSchoolTrucker) => "That one rattled the load, driver. Leave yourself some room and lean on that Jake earlier.",
            ("HarshBraking", CoDriverPersonality.StrictInstructor) => "Harsh braking. That costs points. Plan your stops and brake earlier.",

            ("AggressiveAcceleration", CoDriverPersonality.Professional) => "Heavy throttle detected. Smoother acceleration saves fuel.",
            ("AggressiveAcceleration", CoDriverPersonality.Friendly) => "Little heavy on the go pedal there. Smooth and steady saves diesel.",
            ("AggressiveAcceleration", CoDriverPersonality.OldSchoolTrucker) => "No need to hot-rod it. Roll into it easy and she'll treat you right.",
            ("AggressiveAcceleration", CoDriverPersonality.StrictInstructor) => "Aggressive acceleration. Apply throttle progressively.",

            ("OverRev", CoDriverPersonality.Professional) => "Engine RPM is high. Consider shifting up.",
            ("OverRev", CoDriverPersonality.Friendly) => "Engine's working pretty hard. Maybe grab the next gear?",
            ("OverRev", CoDriverPersonality.OldSchoolTrucker) => "You're winding her up, driver. Grab a gear before you cook that engine.",
            ("OverRev", CoDriverPersonality.StrictInstructor) => "Over-revving. Upshift now.",

            _ => incident.AdvisoryMessage
        };
    }
}
