namespace SharedQuests;

/// <summary>
/// SPT-free quest metadata extracted by the server layer. Ids are raw strings
/// so this file stays unit-testable (no MongoId / SPT types).
/// </summary>
public sealed class QuestMeta
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? TraderId { get; init; }
    /// <summary>Raw quest template Location: a location mongo id, "any", or null.</summary>
    public string? LocationId { get; init; }
    /// <summary>Location targets from AvailableForFinish counter conditions (map ids like "bigmap").</summary>
    public List<string> ConditionLocationIds { get; init; } = [];
    /// <summary>Quest ids from AvailableForStart Quest conditions.</summary>
    public List<string> PrereqQuestIds { get; init; } = [];
}

public sealed class OverviewProfileStatus
{
    public int Status { get; set; }
    public string? LockedReason { get; set; }
}

public sealed class OverviewQuest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Trader { get; init; }
    /// <summary>Canonical lowercase map ids ("bigmap", "factory", ...). Empty = any map.</summary>
    public required List<string> Maps { get; init; }
    /// <summary>Nickname -> status for every profile.</summary>
    public required Dictionary<string, OverviewProfileStatus> Statuses { get; init; }
}

public sealed class OverviewResponse
{
    public required List<string> Profiles { get; init; }
    public required List<OverviewQuest> Quests { get; init; }
}

/// <summary>
/// Pure assembly of the /sharedquests/overview payload. No SPT dependencies.
/// </summary>
public static class OverviewBuilder
{
    // ponytail: trader mongo ids are stable EFT constants; static map avoids a DB dependency.
    private static readonly Dictionary<string, string> TraderNames = new()
    {
        ["54cb50c76803fa8b248b4571"] = "Prapor",
        ["54cb57776803fa99248b456e"] = "Therapist",
        ["579dc571d53a0658a154fbec"] = "Fence",
        ["58330581ace78e27b8b10cee"] = "Skier",
        ["5935c25fb3acc3127c3d8cd9"] = "Peacekeeper",
        ["5a7c2eca46aef81a7ca2145d"] = "Mechanic",
        ["5ac3b934156ae10c4430e83c"] = "Ragman",
        ["5c0647fdd443bc2504c2d371"] = "Jaeger",
        ["638f541a29ffd1183d187f57"] = "Lightkeeper",
        ["6617beeaa9cfa777ca915b7c"] = "Ref",
        ["656f0f98d80a697f855d34b1"] = "BTR Driver",
    };

    /// <summary>
    /// Maps a quest to canonical lowercase map ids. A resolvable template Location wins;
    /// otherwise the AvailableForFinish condition locations are used. Empty = "any map".
    /// </summary>
    public static List<string> DeriveMaps(QuestMeta quest, IReadOnlyDictionary<string, string> locationIdToMapId)
    {
        if (quest.LocationId != null && locationIdToMapId.TryGetValue(quest.LocationId, out var mapId))
            return [Canonical(mapId)];

        return quest.ConditionLocationIds
            .Select(raw => locationIdToMapId.TryGetValue(raw, out var resolved) ? resolved : raw)
            .Select(Canonical)
            .Distinct()
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();
    }

    private static string Canonical(string mapId)
    {
        var m = mapId.ToLowerInvariant();
        return m switch
        {
            "factory4_day" or "factory4_night" => "factory",
            "sandbox_high" => "sandbox",
            _ => m,
        };
    }

    /// <summary>
    /// A quest is included when at least one profile has it AvailableForStart(1),
    /// Started(2), or AvailableForFinish(3). Every profile's status is returned for
    /// included quests; Locked(0) profiles with known prerequisites get a reason
    /// listing each prerequisite with that profile's own status on it.
    /// </summary>
    public static OverviewResponse Build(
        IReadOnlyList<QuestMeta> quests,
        IReadOnlyList<ParsedProfile> profiles,
        IReadOnlyDictionary<string, string> locationIdToMapId)
    {
        var nameById = new Dictionary<string, string>();
        foreach (var q in quests) nameById[q.Id] = q.Name;

        var result = new List<OverviewQuest>();
        foreach (var quest in quests.OrderBy(q => q.Name, StringComparer.Ordinal))
        {
            var statuses = new Dictionary<string, int>();
            foreach (var p in profiles)
                statuses[p.Nickname] = p.QuestStatusByQid.TryGetValue(quest.Id, out var s) ? s : 0;

            if (!statuses.Values.Any(s => s is 1 or 2 or 3)) continue;

            var perProfile = new Dictionary<string, OverviewProfileStatus>();
            foreach (var profile in profiles)
            {
                var status = statuses[profile.Nickname];
                string? reason = null;
                if (status == 0 && quest.PrereqQuestIds.Count > 0)
                {
                    reason = string.Join(", ", quest.PrereqQuestIds.Select(id =>
                    {
                        var prereqName = nameById.TryGetValue(id, out var n) ? n : id;
                        var prereqStatus = profile.QuestStatusByQid.TryGetValue(id, out var ps) ? ps : 0;
                        return $"{prereqName} ({StatusName(prereqStatus)})";
                    }));
                }
                perProfile[profile.Nickname] = new OverviewProfileStatus { Status = status, LockedReason = reason };
            }

            result.Add(new OverviewQuest
            {
                Id = quest.Id,
                Name = quest.Name,
                Trader = quest.TraderId != null && TraderNames.TryGetValue(quest.TraderId, out var trader) ? trader : "",
                Maps = DeriveMaps(quest, locationIdToMapId),
                Statuses = perProfile,
            });
        }

        return new OverviewResponse
        {
            Profiles = profiles.Select(p => p.Nickname).Distinct().ToList(),
            Quests = result,
        };
    }

    private static string StatusName(int status) => status switch
    {
        0 => "Locked", 1 => "Available", 2 => "Started", 3 => "Ready",
        4 => "Done", 5 => "Failed", 6 => "Failed (Retry)", 7 => "Failed",
        8 => "Expired", 9 => "Timed", _ => "Unknown",
    };
}
