using System.Globalization;

namespace SharedQuests;

/// <summary>SPT-free input meta for one quest's detail, extracted by the server layer.</summary>
public sealed class ObjectiveMeta
{
    public required string ConditionId { get; init; }
    public required string Text { get; init; }
    public double? Target { get; init; }
}

public sealed class PrereqMeta
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}

/// <summary>Reward primitives; Kind is "Experience" | "TraderStanding" | "Money" | "Item".</summary>
public sealed class RewardMeta
{
    public required string Kind { get; init; }
    public string? Name { get; init; }
    public double Value { get; init; }
    public int Count { get; init; } = 1;
}

public sealed class QuestDetailMeta
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Trader { get; init; }
    public required List<string> Maps { get; init; }
    public required string Description { get; init; }
    public required List<ObjectiveMeta> Objectives { get; init; }
    public required List<PrereqMeta> Prereqs { get; init; }
    public required List<RewardMeta> Rewards { get; init; }
}

// --- response DTOs (serialized to the client) ---

public sealed class ObjectiveProgress
{
    public int? Count { get; set; }
    public bool Done { get; set; }
}

public sealed class QuestDetailObjective
{
    public required string Text { get; init; }
    public double? Target { get; init; }
    public required Dictionary<string, ObjectiveProgress> Progress { get; init; }
}

public sealed class QuestDetailPrereq
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Dictionary<string, int> Statuses { get; init; }
}

public sealed class QuestDetailResponse
{
    public required string Name { get; init; }
    public required string Trader { get; init; }
    public required List<string> Maps { get; init; }
    public required string Description { get; init; }
    public required List<QuestDetailObjective> Objectives { get; init; }
    public required List<QuestDetailPrereq> Prereqs { get; init; }
    public required List<string> Rewards { get; init; }
}

/// <summary>Pure assembly of the /sharedquests/quest/&lt;id&gt; payload. No SPT dependencies.</summary>
public static class QuestDetailBuilder
{
    // ponytail: mirrors SPT's QuestStatusEnum.Success; duplicated to stay SPT-free.
    private const int StatusSuccess = 4;

    public static QuestDetailResponse Build(QuestDetailMeta meta, IReadOnlyList<ParsedProfile> profiles)
    {
        var objectives = new List<QuestDetailObjective>();
        foreach (var objective in meta.Objectives)
        {
            var progress = new Dictionary<string, ObjectiveProgress>();
            foreach (var profile in profiles) // last write wins on duplicate nicknames
            {
                // completedConditions is only written by the client's post-raid sync, so it
                // lags out-of-raid handovers; quest Success and a full counter also mean done.
                var done = (profile.QuestStatusByQid.TryGetValue(meta.Id, out var status) && status == StatusSuccess)
                           || (profile.CompletedConditionsByQid.TryGetValue(meta.Id, out var completed)
                               && completed.Contains(objective.ConditionId));
                int? count = null;
                if (!done && profile.CounterByConditionId.TryGetValue(objective.ConditionId, out var counter)
                          && counter.SourceId == meta.Id)
                {
                    if (objective.Target is double target && counter.Value >= target)
                        done = true;
                    else
                        count = counter.Value;
                }
                progress[profile.Nickname] = new ObjectiveProgress { Count = count, Done = done };
            }
            objectives.Add(new QuestDetailObjective { Text = objective.Text, Target = objective.Target, Progress = progress });
        }

        var prereqs = new List<QuestDetailPrereq>();
        foreach (var prereq in meta.Prereqs)
        {
            var statuses = new Dictionary<string, int>();
            foreach (var profile in profiles)
                statuses[profile.Nickname] = profile.QuestStatusByQid.TryGetValue(prereq.Id, out var s) ? s : 0;
            prereqs.Add(new QuestDetailPrereq { Id = prereq.Id, Name = prereq.Name, Statuses = statuses });
        }

        return new QuestDetailResponse
        {
            Name = meta.Name,
            Trader = meta.Trader,
            Maps = meta.Maps,
            Description = meta.Description,
            Objectives = objectives,
            Prereqs = prereqs,
            Rewards = meta.Rewards.Select(FormatReward).OfType<string>().ToList(),
        };
    }

    /// <summary>One display line per reward; null = unknown kind, skip.</summary>
    public static string? FormatReward(RewardMeta r) => r.Kind switch
    {
        "Experience" => $"+{r.Value.ToString("N0", CultureInfo.InvariantCulture)} EXP",
        "TraderStanding" => $"{r.Name} rep {(r.Value >= 0 ? "+" : "")}{r.Value.ToString("0.00", CultureInfo.InvariantCulture)}",
        "Money" => $"{r.Count.ToString("N0", CultureInfo.InvariantCulture)} {r.Name}",
        "Item" => r.Count > 1 ? $"{r.Count}× {r.Name}" : r.Name,
        _ => null,
    };
}
