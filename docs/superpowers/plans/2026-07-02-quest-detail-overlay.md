# Quest Detail Overlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clicking a quest row in the planner overlay opens a stacked detail overlay (description, objectives with live per-player progress, prerequisites, rewards) fed by a new on-demand server endpoint.

**Architecture:** A new dynamic route `/sharedquests/quest/<questId>` on the SPT server extracts one quest's template + locale data, merges fresh-from-disk profile progress via a pure `QuestDetailBuilder` (same pure/impure split as `OverviewBuilder`), and returns a `QuestDetailResponse`. The client adds a `QuestDetailPanel` (code-built Unity UI, LootNet style) stacked on top of the existing `QuestPlannerPanel` in the same canvas.

**Tech Stack:** SPT 4.0 server mod (.NET 9, SPTarkov.Server.Core), BepInEx client plugin (netstandard2.1, Unity UGUI + TextMeshPro, Newtonsoft.Json, SPT.Common RequestHandler), xUnit tests.

**Spec:** `docs/superpowers/specs/2026-07-02-quest-detail-overlay-design.md` — read it first.

## Global Constraints

- Work and commit directly on `main`.
- Server code (`Server/`, `Server.Tests/`) is .NET 9, file-scoped namespaces, nullable enabled. Pure logic files must stay SPT-free (no `SPTarkov.*` usings) so they are unit-testable.
- Client code (`Client/`) is netstandard2.1 for Unity: **no `required` members, no records** — plain classes with `{ get; set; }`, block-scoped namespaces, matching `QuestPlannerPanel.cs` style.
- Client UI is built entirely in code (no asset bundles), reusing `QuestPlannerPanel`'s static helpers (`MakeRect`, `MakeTMP`, `SetRect`, `Stretch`) and `Accent` color.
- All number formatting server-side uses `CultureInfo.InvariantCulture` (deterministic tests).
- Tests: xUnit, raw string literals for JSON fixtures, style of `Server.Tests/ProfileParserTests.cs`.
- Build commands: `dotnet test Server.Tests/SharedQuests.Tests.csproj` (verify exact csproj filename with `ls Server.Tests`), `dotnet build Server/SharedQuestsBackend.csproj`, `dotnet build Client/SharedQuests.csproj`.
- Duplicate profile nicknames must not throw — always last-write-wins dictionary loops, never `ToDictionary` on nicknames (see commit 5cbd5d3).

---

### Task 1: ProfileParser — completedConditions and TaskConditionCounters

**Files:**
- Modify: `Server/ProfileParser.cs`
- Test: `Server.Tests/ProfileParserTests.cs`

**Interfaces:**
- Produces (used by Task 2's builder):
  - `ParsedProfile.CompletedConditionsByQid : Dictionary<string, List<string>>` — quest id → completed condition ids (quests with none are absent).
  - `ParsedProfile.CounterByConditionId : Dictionary<string, (string SourceId, int Value)>` — condition id → (owning quest id, current counter value).
  - Both default to empty dictionaries; existing constructors keep compiling (do NOT mark them `required`).

- [ ] **Step 1: Write the failing tests**

Append to `Server.Tests/ProfileParserTests.cs`:

```csharp
    [Fact]
    public void Parse_CompletedConditions_CapturedPerQuest()
    {
        var json = """
        { "characters": { "pmc": { "Info": { "Nickname": "Eve" },
          "Quests": [
            { "qid": "q1", "status": 2, "completedConditions": ["c1", "c2"] },
            { "qid": "q2", "status": 2, "completedConditions": [] }
          ] } } }
        """;
        var p = ProfileParser.Parse(json);
        Assert.Equal(new List<string> { "c1", "c2" }, p!.CompletedConditionsByQid["q1"]);
        Assert.False(p.CompletedConditionsByQid.ContainsKey("q2"));
    }

    [Fact]
    public void Parse_TaskConditionCounters_Captured()
    {
        var json = """
        { "characters": { "pmc": { "Info": { "Nickname": "Eve" },
          "Quests": [],
          "TaskConditionCounters": {
            "c1": { "id": "c1", "sourceId": "q1", "type": "Elimination", "value": 3 }
          } } } }
        """;
        var p = ProfileParser.Parse(json);
        Assert.Equal(("q1", 3), p!.CounterByConditionId["c1"]);
    }

    [Fact]
    public void Parse_CounterMissingIdField_UsesEntryKey()
    {
        var json = """
        { "characters": { "pmc": { "Info": { "Nickname": "Eve" },
          "Quests": [],
          "TaskConditionCounters": { "c9": { "sourceId": "q1", "value": 5 } } } } }
        """;
        var p = ProfileParser.Parse(json);
        Assert.Equal(("q1", 5), p!.CounterByConditionId["c9"]);
    }

    [Fact]
    public void Parse_CounterStringOrFloatValue_Normalized()
    {
        var json = """
        { "characters": { "pmc": { "Info": { "Nickname": "Eve" },
          "Quests": [],
          "TaskConditionCounters": {
            "c1": { "id": "c1", "sourceId": "q1", "value": "4" },
            "c2": { "id": "c2", "sourceId": "q1", "value": 2.0 }
          } } } }
        """;
        var p = ProfileParser.Parse(json);
        Assert.Equal(4, p!.CounterByConditionId["c1"].Value);
        Assert.Equal(2, p.CounterByConditionId["c2"].Value);
    }

    [Fact]
    public void Parse_NoCountersOrCompletedConditions_EmptyMaps()
    {
        var p = ProfileParser.Parse(ValidJson);
        Assert.Empty(p!.CompletedConditionsByQid);
        Assert.Empty(p.CounterByConditionId);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Server.Tests/SharedQuests.Tests.csproj --filter ProfileParserTests -v minimal`
Expected: compile errors — `CompletedConditionsByQid` / `CounterByConditionId` do not exist.

- [ ] **Step 3: Implement**

In `Server/ProfileParser.cs`, extend `ParsedProfile`:

```csharp
public sealed class ParsedProfile
{
    public required string Nickname { get; init; }
    public required Dictionary<string, int> QuestStatusByQid { get; init; }
    /// <summary>Quest id -> completed AvailableForFinish condition ids (absent when none).</summary>
    public Dictionary<string, List<string>> CompletedConditionsByQid { get; init; } = new();
    /// <summary>Condition id -> (owning quest id, current counter value) from TaskConditionCounters.</summary>
    public Dictionary<string, (string SourceId, int Value)> CounterByConditionId { get; init; } = new();
}
```

In `Parse`, inside the existing `Quests` loop (after `byQid[qid] = status;`), collect completed conditions:

```csharp
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
```

(declare `var completedByQid = new Dictionary<string, List<string>>();` next to `byQid`)

After the quests block, parse counters:

```csharp
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
```

Pass both into the returned `ParsedProfile`:

```csharp
            return new ParsedProfile
            {
                Nickname = nickname,
                QuestStatusByQid = byQid,
                CompletedConditionsByQid = completedByQid,
                CounterByConditionId = counters,
            };
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Server.Tests/SharedQuests.Tests.csproj -v minimal`
Expected: all pass (new + all pre-existing).

- [ ] **Step 5: Commit**

```bash
git add Server/ProfileParser.cs Server.Tests/ProfileParserTests.cs
git commit -m "Parse completedConditions and TaskConditionCounters from profiles"
```

---

### Task 2: QuestDetailBuilder (pure) + reward formatting

**Files:**
- Create: `Server/QuestDetailBuilder.cs`
- Test: `Server.Tests/QuestDetailBuilderTests.cs`

**Interfaces:**
- Consumes: `ParsedProfile` from Task 1 (fields listed there).
- Produces (used by Task 3's server layer and mirrored by Task 4's client DTOs):

```csharp
QuestDetailBuilder.Build(QuestDetailMeta meta, IReadOnlyList<ParsedProfile> profiles) : QuestDetailResponse
QuestDetailBuilder.FormatReward(RewardMeta r) : string?   // null = skip line
```

- [ ] **Step 1: Write the failing tests**

Create `Server.Tests/QuestDetailBuilderTests.cs`:

```csharp
using Xunit;

namespace SharedQuests.Tests;

public class QuestDetailBuilderTests
{
    private static ParsedProfile Profile(string nick,
        Dictionary<string, int>? statuses = null,
        Dictionary<string, List<string>>? completed = null,
        Dictionary<string, (string SourceId, int Value)>? counters = null) => new()
    {
        Nickname = nick,
        QuestStatusByQid = statuses ?? new(),
        CompletedConditionsByQid = completed ?? new(),
        CounterByConditionId = counters ?? new(),
    };

    private static QuestDetailMeta Meta(
        List<ObjectiveMeta>? objectives = null,
        List<PrereqMeta>? prereqs = null,
        List<RewardMeta>? rewards = null) => new()
    {
        Id = "quest1",
        Name = "Debut",
        Trader = "Prapor",
        Maps = new List<string> { "bigmap" },
        Description = "Kill some scavs.",
        Objectives = objectives ?? new(),
        Prereqs = prereqs ?? new(),
        Rewards = rewards ?? new(),
    };

    [Fact]
    public void Build_CompletedCondition_MarksDone()
    {
        var meta = Meta(objectives: new() { new ObjectiveMeta { ConditionId = "c1", Text = "Kill 5 scavs", Target = 5 } });
        var alice = Profile("Alice", completed: new() { ["quest1"] = new List<string> { "c1" } });
        var detail = QuestDetailBuilder.Build(meta, new[] { alice });
        var progress = detail.Objectives[0].Progress["Alice"];
        Assert.True(progress.Done);
        Assert.Null(progress.Count);
    }

    [Fact]
    public void Build_MatchingCounter_YieldsCount()
    {
        var meta = Meta(objectives: new() { new ObjectiveMeta { ConditionId = "c1", Text = "Kill 5 scavs", Target = 5 } });
        var alice = Profile("Alice", counters: new() { ["c1"] = ("quest1", 3) });
        var detail = QuestDetailBuilder.Build(meta, new[] { alice });
        Assert.Equal(3, detail.Objectives[0].Progress["Alice"].Count);
        Assert.False(detail.Objectives[0].Progress["Alice"].Done);
    }

    [Fact]
    public void Build_CounterFromOtherQuest_Ignored()
    {
        var meta = Meta(objectives: new() { new ObjectiveMeta { ConditionId = "c1", Text = "Kill 5 scavs", Target = 5 } });
        var alice = Profile("Alice", counters: new() { ["c1"] = ("otherQuest", 3) });
        var detail = QuestDetailBuilder.Build(meta, new[] { alice });
        Assert.Null(detail.Objectives[0].Progress["Alice"].Count);
    }

    [Fact]
    public void Build_NoData_NotDoneNullCount()
    {
        var meta = Meta(objectives: new() { new ObjectiveMeta { ConditionId = "c1", Text = "Find the thing", Target = null } });
        var detail = QuestDetailBuilder.Build(meta, new[] { Profile("Alice") });
        var progress = detail.Objectives[0].Progress["Alice"];
        Assert.False(progress.Done);
        Assert.Null(progress.Count);
    }

    [Fact]
    public void Build_PrereqStatuses_AbsentQuestIsLocked()
    {
        var meta = Meta(prereqs: new() { new PrereqMeta { Id = "q0", Name = "Shooting Cans" } });
        var alice = Profile("Alice", statuses: new() { ["q0"] = 4 });
        var bob = Profile("Bob");
        var detail = QuestDetailBuilder.Build(meta, new[] { alice, bob });
        Assert.Equal(4, detail.Prereqs[0].Statuses["Alice"]);
        Assert.Equal(0, detail.Prereqs[0].Statuses["Bob"]);
    }

    [Fact]
    public void Build_DuplicateNicknames_LastWinsNoThrow()
    {
        var meta = Meta(objectives: new() { new ObjectiveMeta { ConditionId = "c1", Text = "x", Target = 5 } },
                        prereqs: new() { new PrereqMeta { Id = "q0", Name = "y" } });
        var first = Profile("Alice", counters: new() { ["c1"] = ("quest1", 1) });
        var second = Profile("Alice", counters: new() { ["c1"] = ("quest1", 2) });
        var detail = QuestDetailBuilder.Build(meta, new[] { first, second });
        Assert.Equal(2, detail.Objectives[0].Progress["Alice"].Count);
    }

    [Theory]
    [InlineData("Experience", null, 1700, 1, "+1,700 EXP")]
    [InlineData("TraderStanding", "Prapor", 0.02, 1, "Prapor rep +0.02")]
    [InlineData("TraderStanding", "Prapor", -0.01, 1, "Prapor rep -0.01")]
    [InlineData("Money", "Roubles", 0, 45000, "45,000 Roubles")]
    [InlineData("Item", "Bolts", 0, 3, "3× Bolts")]
    [InlineData("Item", "MP-133 12ga shotgun", 0, 1, "MP-133 12ga shotgun")]
    public void FormatReward_KnownKinds(string kind, string? name, double value, int count, string expected)
    {
        var formatted = QuestDetailBuilder.FormatReward(new RewardMeta { Kind = kind, Name = name, Value = value, Count = count });
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void FormatReward_UnknownKind_ReturnsNullAndIsExcludedFromBuild()
    {
        Assert.Null(QuestDetailBuilder.FormatReward(new RewardMeta { Kind = "AssortmentUnlock" }));
        var meta = Meta(rewards: new()
        {
            new RewardMeta { Kind = "Experience", Value = 100 },
            new RewardMeta { Kind = "AssortmentUnlock" },
        });
        var detail = QuestDetailBuilder.Build(meta, new ParsedProfile[0]);
        Assert.Single(detail.Rewards);
        Assert.Equal("+100 EXP", detail.Rewards[0]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Server.Tests/SharedQuests.Tests.csproj --filter QuestDetailBuilderTests -v minimal`
Expected: compile errors — `QuestDetailBuilder`, `QuestDetailMeta`, etc. do not exist.

- [ ] **Step 3: Implement**

Create `Server/QuestDetailBuilder.cs` (pure — no SPT usings, mirrors `OverviewBuilder`'s role):

```csharp
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
    public static QuestDetailResponse Build(QuestDetailMeta meta, IReadOnlyList<ParsedProfile> profiles)
    {
        var objectives = new List<QuestDetailObjective>();
        foreach (var objective in meta.Objectives)
        {
            var progress = new Dictionary<string, ObjectiveProgress>();
            foreach (var profile in profiles) // last write wins on duplicate nicknames
            {
                var done = profile.CompletedConditionsByQid.TryGetValue(meta.Id, out var completed)
                           && completed.Contains(objective.ConditionId);
                int? count = null;
                if (!done && profile.CounterByConditionId.TryGetValue(objective.ConditionId, out var counter)
                          && counter.SourceId == meta.Id)
                    count = counter.Value;
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
            prereqs.Add(new QuestDetailPrereq { Name = prereq.Name, Statuses = statuses });
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Server.Tests/SharedQuests.Tests.csproj -v minimal`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Server/QuestDetailBuilder.cs Server.Tests/QuestDetailBuilderTests.cs
git commit -m "Add pure QuestDetailBuilder with reward formatting"
```

---

### Task 3: Server — quest detail extraction + dynamic route

**Files:**
- Modify: `Server/SharedQuestsBackend.cs`
- Modify: `Server/OverviewBuilder.cs` (expose trader-name lookup)

**Interfaces:**
- Consumes: `QuestDetailBuilder.Build`, `QuestDetailMeta`/`ObjectiveMeta`/`PrereqMeta`/`RewardMeta` (Task 2), `ParsedProfile` fields (Task 1), existing `OverviewBuilder.DeriveMaps`, `_questMetas`, `_locationIdToMapId`.
- Produces: HTTP route `GET /sharedquests/quest/<24-char questId>` returning serialized `QuestDetailResponse`, or SPT null response for unknown ids. Also `OverviewBuilder.TraderName(string? traderId) : string` (used internally).

**IMPORTANT — API verification:** Property names on SPT's quest template model (`Rewards.Success`, reward `Type`/`Value`/`Items`/`Target`, condition `Id`/`Value`) and the `DynamicRouter` base-class signature are best-effort below. Verify against the `SPTarkov.Server.Core` reference assembly (compile errors are the signal; the csproj's `<Reference>`/`<PackageReference>` hints where the dlls live). Adjust names, keep the shape. If no `DynamicRouter` base class exists or it doesn't prefix-match, fall back per spec: static route `/sharedquests/quest` reading the quest id from the POST body (client then uses `RequestHandler.PostJson`; coordinate the change with Task 4's fetch call).

- [ ] **Step 1: Expose trader-name lookup**

In `Server/OverviewBuilder.cs`, add below the `TraderNames` dictionary:

```csharp
    /// <summary>Display name for a trader mongo id; "" when unknown.</summary>
    public static string TraderName(string? traderId) =>
        traderId != null && TraderNames.TryGetValue(traderId, out var name) ? name : "";
```

And change the existing `Build` trader lookup to use it:

```csharp
                Trader = TraderName(quest.TraderId),
```

- [ ] **Step 2: Extract a shared fresh-profile reader**

In `SharedQuestsServer`, extract the profile-reading loop from `GetOverview()` into:

```csharp
    /// <summary>Fresh-from-disk parsed profiles, headless excluded. Never throws.</summary>
    private List<ParsedProfile> ReadProfilesFresh()
    {
        var profiles = new List<ParsedProfile>();
        try
        {
            if (Directory.Exists(ProfilesPath))
            {
                foreach (var profilePath in Directory.GetFiles(ProfilesPath, "*.json"))
                {
                    try
                    {
                        var parsed = ProfileParser.Parse(File.ReadAllText(profilePath));
                        if (parsed == null) continue;
                        if (parsed.Nickname.StartsWith("headless_", StringComparison.OrdinalIgnoreCase)) continue;
                        profiles.Add(parsed);
                    }
                    catch (Exception ex)
                    {
                        logger.Warning($"[SharedQuests] Error reading profile {profilePath}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error($"[SharedQuests] Error reading profiles: {ex.Message}");
        }
        return profiles;
    }
```

`GetOverview()` becomes:

```csharp
    public OverviewResponse GetOverview()
    {
        return OverviewBuilder.Build(_questMetas, ReadProfilesFresh(), _locationIdToMapId);
    }
```

- [ ] **Step 3: Inject LocaleService and implement GetQuestDetail**

Add `LocaleService localeService` to the `SharedQuestsServer` primary constructor (namespace `SPTarkov.Server.Core.Services`), then add:

```csharp
    // Money item tpls: roubles, dollars, euros
    private static readonly HashSet<string> MoneyTpls =
    [
        "5449016a4bdc2d6f028b456f", "5696686a4bdc2da3298b456a", "569668774bdc2da2298b4568",
    ];

    /// <summary>Detail payload for one quest, or null when the id is unknown.</summary>
    public QuestDetailResponse? GetQuestDetail(string questId)
    {
        var meta = _questMetas.FirstOrDefault(m => m.Id == questId);
        var quest = questHelper.GetQuestsFromDb().FirstOrDefault(q => q.Id.ToString() == questId);
        if (meta == null || quest == null) return null;

        var locales = localeService.GetLocaleDb();
        string L(string key, string fallback) =>
            locales.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;

        var objectives = new List<ObjectiveMeta>();
        foreach (var condition in quest.Conditions?.AvailableForFinish ?? [])
        {
            var conditionId = condition.Id?.ToString();
            if (string.IsNullOrEmpty(conditionId)) continue;
            double? target = null;
            if (condition.Value != null && double.TryParse(condition.Value.ToString(), out var t) && t > 0)
                target = t;
            objectives.Add(new ObjectiveMeta { ConditionId = conditionId, Text = L(conditionId, conditionId), Target = target });
        }

        var rewards = new List<RewardMeta>();
        try
        {
            foreach (var reward in quest.Rewards?.Success ?? [])
            {
                var kind = reward.Type?.ToString();
                if (kind == "Experience")
                {
                    if (double.TryParse(reward.Value?.ToString(), out var xp))
                        rewards.Add(new RewardMeta { Kind = "Experience", Value = xp });
                }
                else if (kind == "TraderStanding")
                {
                    if (double.TryParse(reward.Value?.ToString(), out var rep))
                        rewards.Add(new RewardMeta
                        {
                            Kind = "TraderStanding",
                            Name = OverviewBuilder.TraderName(reward.Target?.ToString()),
                            Value = rep,
                        });
                }
                else if (kind == "Item")
                {
                    var firstItem = reward.Items?.FirstOrDefault();
                    var tpl = firstItem?.Template.ToString();
                    if (tpl == null) continue;
                    var count = 1;
                    if (double.TryParse(reward.Value?.ToString(), out var c) && c >= 1) count = (int)c;
                    var isMoney = MoneyTpls.Contains(tpl);
                    rewards.Add(new RewardMeta
                    {
                        Kind = isMoney ? "Money" : "Item",
                        Name = L($"{tpl} Name", tpl),
                        Count = count,
                    });
                }
                // other kinds (AssortmentUnlock, Skill, ...) intentionally skipped
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[SharedQuests] Error extracting rewards for {questId}: {ex.Message}");
        }

        var detailMeta = new QuestDetailMeta
        {
            Id = meta.Id,
            Name = L($"{questId} name", meta.Name),
            Trader = OverviewBuilder.TraderName(meta.TraderId),
            Maps = OverviewBuilder.DeriveMaps(meta, _locationIdToMapId),
            Description = L($"{questId} description", ""),
            Objectives = objectives,
            Prereqs = meta.PrereqQuestIds
                .Select(id => new PrereqMeta { Id = id, Name = _questMetas.FirstOrDefault(m => m.Id == id)?.Name ?? id })
                .ToList(),
            Rewards = rewards,
        };

        return QuestDetailBuilder.Build(detailMeta, ReadProfilesFresh());
    }
```

Notes for the implementer:
- `TraderStanding` rewards: the trader id may live on `reward.Target` or a `TraderId` property — check the model, use whichever exists.
- Reward `Items` entries: the item template id property may be `Template`, `Tpl`, or similar; for stacked items a count may live in `Upd.StackObjectsCount` — if `reward.Value` parses to < 1 but a stack count exists, prefer the stack count.
- `L($"{questId} name", ...)`: locale name wins over the template `QuestName` (matches how the game displays names).

- [ ] **Step 4: Add the dynamic router**

In `Server/SharedQuestsBackend.cs`, add alongside `SharedQuestsRouter`:

```csharp
/// <summary>Dynamic (prefix-match) router for per-quest detail: /sharedquests/quest/&lt;id&gt;.</summary>
[Injectable]
public class SharedQuestsDynamicRouter : DynamicRouter
{
    private static JsonUtil? _jsonUtil;
    private static HttpResponseUtil? _httpResponseUtil;
    private static SharedQuestsServer? _server;

    public SharedQuestsDynamicRouter(JsonUtil jsonUtil, HttpResponseUtil httpResponseUtil)
        : base(jsonUtil, GetCustomRoutes())
    {
        _jsonUtil = jsonUtil;
        _httpResponseUtil = httpResponseUtil;
    }

    public void SetServer(SharedQuestsServer server) => _server = server;

    private static List<RouteAction> GetCustomRoutes()
    {
        return
        [
            new RouteAction(
                "/sharedquests/quest/",
                static async (url, info, sessionId, output) => await HandleQuestDetail(url)
            )
        ];
    }

    private static ValueTask<string> HandleQuestDetail(string url)
    {
        try
        {
            var questId = url;
            var slash = questId.LastIndexOf('/');
            if (slash >= 0) questId = questId.Substring(slash + 1);
            var query = questId.IndexOf('?');
            if (query >= 0) questId = questId.Substring(0, query);

            var detail = _server?.GetQuestDetail(questId);
            return detail == null
                ? new ValueTask<string>(_httpResponseUtil!.NullResponse())
                : new ValueTask<string>(_jsonUtil!.Serialize(detail)!);
        }
        catch (Exception)
        {
            return new ValueTask<string>(_httpResponseUtil!.NullResponse());
        }
    }
}
```

Wire it: add `SharedQuestsDynamicRouter dynamicRouter` to the `SharedQuestsServer` primary constructor and call `dynamicRouter.SetServer(this);` in `OnLoad()` next to the existing `router.SetServer(this)`. Log the new endpoint in the existing "Endpoints available" line.

- [ ] **Step 5: Build and run all server tests**

Run: `dotnet build Server/SharedQuestsBackend.csproj && dotnet test Server.Tests/SharedQuests.Tests.csproj -v minimal`
Expected: build succeeds (fix any SPT model property-name mismatches per the note above), all tests pass.

- [ ] **Step 6: Commit**

```bash
git add Server/SharedQuestsBackend.cs Server/OverviewBuilder.cs
git commit -m "Add /sharedquests/quest/<id> detail endpoint"
```

---

### Task 4: Client — DTOs + QuestDetailPanel

**Files:**
- Create: `Client/QuestDetailPanel.cs`
- Modify: `Client/QuestPlannerPanel.cs` (one line: make `MapNames` internal)

**Interfaces:**
- Consumes: `QuestPlannerPanel.MakeRect/MakeTMP/SetRect/Stretch` (already `internal static`), `QuestPlannerPanel.Accent`, `QuestPlannerPanel.MapNames` (made internal in this task), `Plugin.GetStatusName(int)`, `Plugin.GetStatusColor(int)` (returns hex string), `Plugin.LogSource`, `RequestHandler.GetJson`, `Settings.IsProfileVisible(string)`.
- Produces (used by Task 5):

```csharp
public QuestDetailPanel(Transform canvasRoot)   // builds hidden UI under the planner canvas
public void ShowFor(string questId, List<string> visibleProfiles)
public void Hide()
public bool IsOpen { get; }
```

- [ ] **Step 1: Make MapNames internal**

In `Client/QuestPlannerPanel.cs`:

```csharp
        internal static readonly Dictionary<string, string> MapNames = new Dictionary<string, string>
```

(was `private static readonly`.)

- [ ] **Step 2: Create QuestDetailPanel.cs**

Create `Client/QuestDetailPanel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using SPT.Common.Http;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SharedQuests
{
    /// <summary>Detail payload from /sharedquests/quest/&lt;id&gt; (mirrors server DTOs).</summary>
    public class QuestDetailObjectiveProgress
    {
        public int? Count { get; set; }
        public bool Done { get; set; }
    }

    public class QuestDetailObjective
    {
        public string Text { get; set; }
        public double? Target { get; set; }
        public Dictionary<string, QuestDetailObjectiveProgress> Progress { get; set; }
    }

    public class QuestDetailPrereq
    {
        public string Name { get; set; }
        public Dictionary<string, int> Statuses { get; set; }
    }

    public class QuestDetailResponse
    {
        public string Name { get; set; }
        public string Trader { get; set; }
        public List<string> Maps { get; set; }
        public string Description { get; set; }
        public List<QuestDetailObjective> Objectives { get; set; }
        public List<QuestDetailPrereq> Prereqs { get; set; }
        public List<string> Rewards { get; set; }
    }

    /// <summary>
    /// Stacked overlay showing one quest's details on top of the planner.
    /// Plain class (not a MonoBehaviour): ESC handling lives in QuestPlannerPanel.Update.
    /// </summary>
    public class QuestDetailPanel
    {
        private const float PanelW = 900f;
        private const float HeaderH = 80f;

        private readonly GameObject _root;
        private readonly RectTransform _contentRt;
        private readonly Transform _content;
        private readonly TextMeshProUGUI _title;
        private readonly TextMeshProUGUI _sub;
        private readonly TextMeshProUGUI _message;
        private readonly GameObject _retry;

        private string _questId;
        private List<string> _profiles = new List<string>();

        public bool IsOpen => _root.activeSelf;

        public QuestDetailPanel(Transform canvasRoot)
        {
            _root = QuestPlannerPanel.MakeRect("DetailRoot", canvasRoot);
            QuestPlannerPanel.Stretch(_root.GetComponent<RectTransform>());

            // Dim backdrop; clicking it closes only the detail panel
            var overlay = QuestPlannerPanel.MakeRect("Overlay", _root.transform);
            QuestPlannerPanel.Stretch(overlay.GetComponent<RectTransform>());
            overlay.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);
            var overlayBtn = overlay.AddComponent<Button>();
            overlayBtn.transition = Selectable.Transition.None;
            overlayBtn.onClick.AddListener(Hide);

            var panelGo = QuestPlannerPanel.MakeRect("Panel", _root.transform);
            var panelRt = panelGo.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.08f);
            panelRt.anchorMax = new Vector2(0.5f, 0.92f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(PanelW, 0f);
            panelGo.AddComponent<Image>().color = new Color(0.06f, 0.06f, 0.07f, 1f);
            var panelBlock = panelGo.AddComponent<Button>();
            panelBlock.transition = Selectable.Transition.None;

            var topBar = QuestPlannerPanel.MakeRect("TopBar", panelGo.transform);
            var topBarRt = topBar.GetComponent<RectTransform>();
            topBarRt.anchorMin = new Vector2(0f, 1f);
            topBarRt.anchorMax = new Vector2(1f, 1f);
            topBarRt.pivot = Vector2.up;
            topBarRt.sizeDelta = new Vector2(0f, 3f);
            topBar.AddComponent<Image>().color = QuestPlannerPanel.Accent;

            _title = QuestPlannerPanel.MakeTMP("Title", panelGo.transform, 24f, FontStyles.Bold, TextAlignmentOptions.Left);
            _title.color = QuestPlannerPanel.Accent;
            _title.characterSpacing = 3f;
            _title.overflowMode = TextOverflowModes.Ellipsis;
            _title.enableWordWrapping = false;
            QuestPlannerPanel.SetRect(_title.rectTransform,
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(24f, -HeaderH + 14f), offsetMax: new Vector2(-60f, -14f));

            _sub = QuestPlannerPanel.MakeTMP("Sub", panelGo.transform, 12f, FontStyles.Normal, TextAlignmentOptions.Left);
            _sub.color = new Color(0.35f, 0.35f, 0.35f);
            _sub.characterSpacing = 2f;
            QuestPlannerPanel.SetRect(_sub.rectTransform,
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(24f, -HeaderH + 2f), offsetMax: new Vector2(-60f, -HeaderH + 18f));

            var closeGo = QuestPlannerPanel.MakeRect("Close", panelGo.transform);
            var closeRt = closeGo.GetComponent<RectTransform>();
            closeRt.anchorMin = new Vector2(1f, 1f);
            closeRt.anchorMax = new Vector2(1f, 1f);
            closeRt.pivot = new Vector2(1f, 1f);
            closeRt.anchoredPosition = new Vector2(-12f, -12f);
            closeRt.sizeDelta = new Vector2(32f, 32f);
            closeGo.AddComponent<Image>().color = Color.clear;
            var closeLabel = QuestPlannerPanel.MakeTMP("X", closeGo.transform, 20f, FontStyles.Bold, TextAlignmentOptions.Center);
            QuestPlannerPanel.Stretch(closeLabel.rectTransform);
            closeLabel.text = "✕";
            closeLabel.color = new Color(0.6f, 0.6f, 0.6f);
            var closeBtn = closeGo.AddComponent<Button>();
            closeBtn.transition = Selectable.Transition.None;
            closeBtn.onClick.AddListener(Hide);

            var divider = QuestPlannerPanel.MakeRect("Divider", panelGo.transform);
            var dividerRt = divider.GetComponent<RectTransform>();
            dividerRt.anchorMin = new Vector2(0f, 1f);
            dividerRt.anchorMax = new Vector2(1f, 1f);
            dividerRt.pivot = Vector2.up;
            dividerRt.anchoredPosition = new Vector2(0f, -HeaderH);
            dividerRt.sizeDelta = new Vector2(0f, 1f);
            divider.AddComponent<Image>().color = new Color(
                QuestPlannerPanel.Accent.r, QuestPlannerPanel.Accent.g, QuestPlannerPanel.Accent.b, 0.25f);

            // Scrollable content (same pattern as the planner)
            var scrollGo = QuestPlannerPanel.MakeRect("Scroll", panelGo.transform);
            var scrollRt = scrollGo.GetComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = new Vector2(0f, 12f);
            scrollRt.offsetMax = new Vector2(0f, -(HeaderH + 1f));
            var scrollRect = scrollGo.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.scrollSensitivity = 30f;

            var viewportGo = QuestPlannerPanel.MakeRect("Viewport", scrollGo.transform);
            QuestPlannerPanel.Stretch(viewportGo.GetComponent<RectTransform>());
            viewportGo.AddComponent<RectMask2D>();
            viewportGo.AddComponent<Image>().color = Color.clear; // scroll catch-all
            scrollRect.viewport = viewportGo.GetComponent<RectTransform>();

            var contentGo = QuestPlannerPanel.MakeRect("Content", viewportGo.transform);
            _contentRt = contentGo.GetComponent<RectTransform>();
            _contentRt.anchorMin = new Vector2(0f, 1f);
            _contentRt.anchorMax = new Vector2(1f, 1f);
            _contentRt.pivot = new Vector2(0.5f, 1f);
            var layout = contentGo.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.padding = new RectOffset(24, 24, 8, 16);
            layout.spacing = 6f;
            var fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scrollRect.content = _contentRt;
            _content = contentGo.transform;

            _message = QuestPlannerPanel.MakeTMP("Message", panelGo.transform, 16f, FontStyles.Normal, TextAlignmentOptions.Center);
            _message.color = new Color(0.45f, 0.45f, 0.45f);
            QuestPlannerPanel.SetRect(_message.rectTransform,
                anchorMin: new Vector2(0f, 0.45f), anchorMax: new Vector2(1f, 0.6f),
                offsetMin: new Vector2(24f, 0f), offsetMax: new Vector2(-24f, 0f));
            _message.gameObject.SetActive(false);

            var retryGo = QuestPlannerPanel.MakeRect("Retry", panelGo.transform);
            var retryRt = retryGo.GetComponent<RectTransform>();
            retryRt.anchorMin = new Vector2(0.5f, 0.38f);
            retryRt.anchorMax = new Vector2(0.5f, 0.38f);
            retryRt.pivot = new Vector2(0.5f, 0.5f);
            retryRt.sizeDelta = new Vector2(120f, 32f);
            retryGo.AddComponent<Image>().color = new Color(
                QuestPlannerPanel.Accent.r, QuestPlannerPanel.Accent.g, QuestPlannerPanel.Accent.b, 0.2f);
            var retryLabel = QuestPlannerPanel.MakeTMP("Label", retryGo.transform, 14f, FontStyles.Bold, TextAlignmentOptions.Center);
            QuestPlannerPanel.Stretch(retryLabel.rectTransform);
            retryLabel.text = "RETRY";
            retryLabel.color = QuestPlannerPanel.Accent;
            var retryBtn = retryGo.AddComponent<Button>();
            retryBtn.transition = Selectable.Transition.None;
            retryBtn.onClick.AddListener(Refresh);
            _retry = retryGo;
            _retry.SetActive(false);

            _root.SetActive(false);
        }

        public void ShowFor(string questId, List<string> visibleProfiles)
        {
            _questId = questId;
            _profiles = visibleProfiles ?? new List<string>();
            _root.SetActive(true);
            Refresh();
        }

        public void Hide() => _root.SetActive(false);

        private void Refresh()
        {
            ClearContent();
            ShowMessage("Loading...", showRetry: false);
            _title.text = "QUEST DETAILS";
            _sub.text = "";

            QuestDetailResponse data;
            try
            {
                var response = RequestHandler.GetJson($"/sharedquests/quest/{_questId}");
                data = JsonConvert.DeserializeObject<QuestDetailResponse>(response);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"SharedQuests: Error fetching quest detail: {ex.Message}");
                data = null;
            }

            if (data == null)
            {
                ShowMessage("Couldn't load quest details", showRetry: true);
                return;
            }

            HideMessage();
            Render(data);
        }

        private void Render(QuestDetailResponse data)
        {
            _title.text = (data.Name ?? "").ToUpperInvariant();

            var maps = (data.Maps ?? new List<string>())
                .Select(m => QuestPlannerPanel.MapNames.TryGetValue(m, out var n) ? n : m.ToUpperInvariant())
                .ToList();
            var subParts = new List<string>();
            if (!string.IsNullOrEmpty(data.Trader)) subParts.Add(data.Trader.ToUpperInvariant());
            if (maps.Count > 0) subParts.Add(string.Join(", ", maps));
            subParts.Add("ESC TO GO BACK");
            _sub.text = string.Join("  ·  ", subParts);

            if (!string.IsNullOrEmpty(data.Description))
                AddParagraph(data.Description, 14f, new Color(0.62f, 0.62f, 0.62f));

            var objectives = data.Objectives ?? new List<QuestDetailObjective>();
            if (objectives.Count > 0)
            {
                AddSectionHeader("OBJECTIVES");
                foreach (var objective in objectives)
                {
                    var fragments = _profiles
                        .Select(p => ObjectiveFragment(p, objective))
                        .ToList();
                    var line = $"<color=#CCCCCC>•  {objective.Text}</color>";
                    if (fragments.Count > 0)
                        line += $"\n<line-indent=20>{string.Join("   ", fragments)}</line-indent>";
                    AddParagraph(line, 14f, Color.white);
                }
            }

            var prereqs = data.Prereqs ?? new List<QuestDetailPrereq>();
            if (prereqs.Count > 0)
            {
                AddSectionHeader("PREREQUISITES");
                foreach (var prereq in prereqs)
                {
                    var fragments = _profiles.Select(p =>
                    {
                        var status = 0;
                        if (prereq.Statuses != null) prereq.Statuses.TryGetValue(p, out status);
                        return $"<color={Plugin.GetStatusColor(status)}>{p} {Plugin.GetStatusName(status)}</color>";
                    });
                    AddParagraph($"<color=#CCCCCC>•  {prereq.Name}</color>   {string.Join("   ", fragments)}",
                        14f, Color.white);
                }
            }

            var rewards = data.Rewards ?? new List<string>();
            if (rewards.Count > 0)
            {
                AddSectionHeader("REWARDS");
                AddParagraph(string.Join("\n", rewards.Select(r => $"•  {r}")), 14f, new Color(0.62f, 0.62f, 0.62f));
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_contentRt);
        }

        private static string ObjectiveFragment(string profile, QuestDetailObjective objective)
        {
            QuestDetailObjectiveProgress progress = null;
            if (objective.Progress != null) objective.Progress.TryGetValue(profile, out progress);
            if (progress != null && progress.Done)
                return $"<color=#32CD32>{profile} ✓</color>";
            if (progress != null && progress.Count.HasValue)
            {
                var counter = objective.Target.HasValue
                    ? $"{progress.Count.Value}/{(int)objective.Target.Value}"
                    : progress.Count.Value.ToString();
                return $"<color=#FFA500>{profile} {counter}</color>";
            }
            return $"<color=#555555>{profile} –</color>";
        }

        private void AddSectionHeader(string text)
        {
            var label = QuestPlannerPanel.MakeTMP("Section", _content, 13f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            label.text = text;
            label.color = QuestPlannerPanel.Accent;
            label.characterSpacing = 3f;
            var le = label.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 30f;
            le.flexibleHeight = 0f;
        }

        private void AddParagraph(string text, float fontSize, Color color)
        {
            var label = QuestPlannerPanel.MakeTMP("Para", _content, fontSize, FontStyles.Normal, TextAlignmentOptions.TopLeft);
            label.text = text;
            label.color = color;
            label.enableWordWrapping = true;
        }

        private void ShowMessage(string text, bool showRetry)
        {
            _message.text = text;
            _message.gameObject.SetActive(true);
            _retry.SetActive(showRetry);
        }

        private void HideMessage()
        {
            _message.gameObject.SetActive(false);
            _retry.SetActive(false);
        }

        private void ClearContent()
        {
            for (int i = _content.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_content.GetChild(i).gameObject);
        }
    }
}
```

Note: if `<line-indent>` renders literally in the objective progress line (older TMP versions), replace it with four spaces of plain indentation.

- [ ] **Step 3: Build**

Run: `dotnet build Client/SharedQuests.csproj`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add Client/QuestDetailPanel.cs Client/QuestPlannerPanel.cs
git commit -m "Add QuestDetailPanel stacked overlay"
```

---

### Task 5: Wire detail panel into the planner

**Files:**
- Modify: `Client/QuestPlannerPanel.cs`

**Interfaces:**
- Consumes: `QuestDetailPanel` (Task 4 signatures).
- Produces: clicking any quest row opens the detail overlay; ESC/click-outside close the detail first, then the planner.

- [ ] **Step 1: Create the panel and handle ESC ordering**

In `QuestPlannerPanel`, add a field:

```csharp
        private QuestDetailPanel _detail;
```

At the end of `BuildUI()` (after `_root.SetActive(false);`), create it — sibling created after `PlannerRoot`, so it draws on top:

```csharp
            _detail = new QuestDetailPanel(transform);
```

Replace `Update()`:

```csharp
        private void Update()
        {
            if (!_visible || !Input.GetKeyDown(KeyCode.Escape)) return;
            if (_detail != null && _detail.IsOpen) _detail.Hide();
            else Hide();
        }
```

In `Hide()`, also close the detail so it never lingers after the planner closes:

```csharp
        public void Hide()
        {
            if (!_visible) return;
            _visible = false;
            _detail?.Hide();
            _root.SetActive(false);
        }
```

- [ ] **Step 2: Make quest rows clickable**

In `BuildQuestRow`, after the row is created (`var row = MakeRow("Quest", RowH, parent);`), add:

```csharp
            row.AddComponent<Image>().color = Color.clear; // raycast target for the button
            var rowBtn = row.AddComponent<Button>();
            rowBtn.transition = Selectable.Transition.None;
            var questId = quest.Id;
            rowBtn.onClick.AddListener(() => _detail.ShowFor(questId, profiles));
```

- [ ] **Step 3: Build**

Run: `dotnet build Client/SharedQuests.csproj && dotnet build SharedQuests.sln`
Expected: builds succeed; server tests still pass via `dotnet test Server.Tests/SharedQuests.Tests.csproj -v minimal`.

- [ ] **Step 4: Commit**

```bash
git add Client/QuestPlannerPanel.cs
git commit -m "Open quest detail overlay from planner rows"
```

---

## Manual verification (after all tasks — requires the game)

Not automatable; hand back to the user with this checklist:

1. Install built dlls (`dist/` layout per README), start SPT server + client.
2. Open the planner, click a quest row → detail overlay appears on top, planner visible behind.
3. Verify description, objectives (✓ / n/m / – per visible profile), prerequisites with colored statuses, rewards.
4. ESC closes detail only (planner keeps scroll position); second ESC closes planner. Click-outside behaves the same. ✕ works on both.
5. Stop the SPT server, click a quest → "Couldn't load quest details" + RETRY; restart server, RETRY loads.
6. Toggle a profile off in F12, reopen detail → hidden profile's fragments gone.
