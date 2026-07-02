using System.Text.Json;

namespace SharedQuests;

/// <summary>
/// Parsed profile: nickname plus a questId -> status-code map for O(1) lookup.
/// </summary>
public sealed class ParsedProfile
{
    public required string Nickname { get; init; }
    public required Dictionary<string, int> QuestStatusByQid { get; init; }
    /// <summary>Quest id -> completed AvailableForFinish condition ids (absent when none).</summary>
    public Dictionary<string, List<string>> CompletedConditionsByQid { get; init; } = new();
    /// <summary>Condition id -> (owning quest id, current counter value) from TaskConditionCounters.</summary>
    public Dictionary<string, (string SourceId, int Value)> CounterByConditionId { get; init; } = new();
}

/// <summary>
/// Pure profile-file parsing — no SPT dependencies, so it is unit-testable.
/// </summary>
public static class ProfileParser
{
    // ponytail: mirrors SPT's QuestStatusEnum integer values; duplicated here so
    // this class needs no SPT reference and stays testable.
    private static readonly Dictionary<string, int> StatusNameToInt = new()
    {
        ["Locked"] = 0, ["AvailableForStart"] = 1, ["Started"] = 2,
        ["AvailableForFinish"] = 3, ["Success"] = 4, ["Fail"] = 5,
        ["FailRestartable"] = 6, ["MarkedAsFailed"] = 7, ["Expired"] = 8,
        ["AvailableAfter"] = 9,
    };

    /// <summary>
    /// Parse a profile JSON string. Returns null if the JSON is invalid or does
    /// not contain characters.pmc.Info.Nickname. The status of each quest may be
    /// a JSON number or a status-name string; both are normalized to an int.
    /// </summary>
    public static ParsedProfile? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("characters", out var characters)) return null;
            if (!characters.TryGetProperty("pmc", out var pmc)) return null;
            if (!pmc.TryGetProperty("Info", out var info)) return null;
            if (!info.TryGetProperty("Nickname", out var nicknameElement)) return null;

            var nickname = nicknameElement.GetString();
            if (string.IsNullOrEmpty(nickname)) return null;

            var byQid = new Dictionary<string, int>();
            var completedByQid = new Dictionary<string, List<string>>();
            if (pmc.TryGetProperty("Quests", out var quests) && quests.ValueKind == JsonValueKind.Array)
            {
                foreach (var q in quests.EnumerateArray())
                {
                    if (!q.TryGetProperty("qid", out var qidEl)) continue;
                    if (!q.TryGetProperty("status", out var statusEl)) continue;

                    var qid = qidEl.GetString();
                    if (string.IsNullOrEmpty(qid)) continue;

                    int status;
                    if (statusEl.ValueKind == JsonValueKind.Number)
                        status = statusEl.GetInt32();
                    else if (!StatusNameToInt.TryGetValue(statusEl.GetString() ?? "Locked", out status))
                        status = 0; // unknown name -> Locked

                    byQid[qid] = status; // last write wins on duplicate qid

                    if (q.TryGetProperty("completedConditions", out var ccEl) && ccEl.ValueKind == JsonValueKind.Array)
                    {
                        var completed = new List<string>();
                        foreach (var c in ccEl.EnumerateArray())
                        {
                            if (c.ValueKind != JsonValueKind.String) continue;
                            var cid = c.GetString();
                            if (!string.IsNullOrEmpty(cid)) completed.Add(cid);
                        }
                        if (completed.Count > 0) completedByQid[qid] = completed;
                    }
                }
            }

            var counters = new Dictionary<string, (string SourceId, int Value)>();
            if (pmc.TryGetProperty("TaskConditionCounters", out var tcc) && tcc.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in tcc.EnumerateObject())
                {
                    var v = entry.Value;
                    if (v.ValueKind != JsonValueKind.Object) continue;

                    var condId = v.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString() : entry.Name;
                    if (string.IsNullOrEmpty(condId)) condId = entry.Name;

                    var sourceId = v.TryGetProperty("sourceId", out var srcEl) && srcEl.ValueKind == JsonValueKind.String
                        ? srcEl.GetString() ?? "" : "";

                    var value = 0;
                    if (v.TryGetProperty("value", out var valEl))
                    {
                        if (valEl.ValueKind == JsonValueKind.Number) value = (int)valEl.GetDouble();
                        else if (valEl.ValueKind == JsonValueKind.String && int.TryParse(valEl.GetString(), out var parsed)) value = parsed;
                    }

                    counters[condId] = (sourceId, value);
                }
            }

            return new ParsedProfile
            {
                Nickname = nickname,
                QuestStatusByQid = byQid,
                CompletedConditionsByQid = completedByQid,
                CounterByConditionId = counters,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
