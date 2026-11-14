using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.AI;

/// <summary>
/// Moves a trucker relationship forward based on the quality and continuity of conversations,
/// not just message count:
/// - substantive messages (more words, real back-and-forth) earn more than "ten-four";
/// - coming back on a different day earns a continuity bonus;
/// - spamming many messages in a short time earns little.
/// Also keeps a short local memory list (no extra AI call needed).
/// </summary>
public static class RelationshipManager
{
    // Points needed to reach each level.
    private static readonly (RelationshipLevel Level, int Points)[] Thresholds =
    {
        (RelationshipLevel.OldFriend, 160),
        (RelationshipLevel.RoadBuddy, 90),
        (RelationshipLevel.RoadAcquaintance, 45),
        (RelationshipLevel.FamiliarVoice, 15),
        (RelationshipLevel.Stranger, 0)
    };

    public static int ScoreExchange(string playerText, DateTimeOffset now, DateTimeOffset? lastSpoken)
    {
        int words = playerText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        int points = words switch { >= 15 => 4, >= 7 => 3, >= 3 => 2, _ => 1 };

        if (lastSpoken is { } last)
        {
            var gap = now - last;
            if (gap < TimeSpan.FromSeconds(20)) points = Math.Min(points, 1);   // rapid-fire
            else if (gap > TimeSpan.FromHours(12)) points += 3;                 // came back another day
        }
        return points;
    }

    public static RelationshipLevel LevelFor(int points) =>
        Thresholds.First(t => points >= t.Points).Level;

    /// <summary>Applies one exchange. Returns true if the relationship level went up.</summary>
    public static bool ApplyExchange(TruckerPersonality trucker, string playerText, string replyText, string where, DateTimeOffset now, int maxMemories)
    {
        var before = trucker.Relationship;
        lock (trucker.Sync)
        {
            trucker.RelationshipPoints += ScoreExchange(playerText, now, trucker.LastSpokenAt);
            trucker.Relationship = LevelFor(trucker.RelationshipPoints);
            trucker.ConversationCount++;
            trucker.LastSpokenAt = now;

            string memory = $"{now:MMM d}: player said \"{Trim(playerText, 90)}\"{where}; you replied \"{Trim(replyText, 70)}\"";
            trucker.SummarizedMemories.Add(memory);
            if (trucker.SummarizedMemories.Count > maxMemories)
                trucker.SummarizedMemories.RemoveRange(0, trucker.SummarizedMemories.Count - maxMemories);
        }

        return trucker.Relationship > before;
    }

    private static string Trim(string s, int max)
    {
        s = s.Replace('"', '\'').Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + "...";
    }
}
