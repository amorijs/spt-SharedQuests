# Quest Planner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An in-game overlay panel, opened from a main-menu taskbar button, showing all profiles' quest statuses grouped by map so a group can pick the best map to play together.

**Architecture:** The SPT server mod gains a `/sharedquests/overview` endpoint whose payload is assembled by a pure, unit-tested `OverviewBuilder` (quest metadata + per-profile statuses + map derivation + locked reasons). The BepInEx client gains a taskbar button (LootNet `RaidHistoryMenuButton` pattern) and a code-built overlay panel (LootNet `RaidHistoryDisplay` pattern) that fetches the payload and renders map-grouped rows. Spec: `docs/superpowers/specs/2026-07-02-quest-planner-design.md`.

**Tech Stack:** C# — server: net9.0 SPT 4.0.5 mod (`SPTarkov.Server.Core`), tests: xunit; client: netstandard2.1 BepInEx plugin, Unity UI + TextMeshPro, HarmonyLib.

## Global Constraints

- Work and commit directly on `main`. No branches, no PRs.
- Existing `/sharedquests/statuses` endpoint and quest-description injection must keep working unchanged.
- `OverviewBuilder.cs` must have **zero SPT dependencies** (BCL only + `ParsedProfile`) — it is compiled into the test project by file link, same as `ProfileParser.cs`.
- Server test command: `dotnet test Server.Tests/SharedQuests.Tests.csproj --nologo` (run from repo root `C:\Users\chris\projects\shared-quests`).
- Server build: `dotnet build Server/SharedQuestsBackend.csproj --nologo`. Client build: `dotnet build Client/SharedQuests.csproj --nologo` (references DLLs under `C:\SPT`, which exists on this machine).
- Client code style: `Nullable` is **disabled** in the client project — do not use `?.` on value types or nullable annotations there. Server has nullable enabled.
- Match existing log prefix conventions: `[SharedQuests]` on server, `SharedQuests:` on client.

## Verified API facts (do not re-derive)

These were verified against `SPTarkov.Server.Core` 4.0.5 by reflection; trust them:

- `Quest` (`SPTarkov.Server.Core.Models.Eft.Common.Tables`): `MongoId Id`, `string QuestName`, `string Name`, `MongoId TraderId`, `string Location`, `QuestConditionTypes Conditions`.
- `QuestConditionTypes`: `List<QuestCondition> AvailableForStart`, `List<QuestCondition> AvailableForFinish`.
- `QuestCondition`: `string ConditionType`, `ListOrT<string> Target`, `QuestConditionCounter Counter`.
- `QuestConditionCounter`: `List<QuestCondition> Conditions`.
- `DatabaseService` (`SPTarkov.Server.Core.Services`): `Locations GetLocations()`.
- `Locations` (`SPTarkov.Server.Core.Models.Spt.Server`): one property of type `Location` (`SPTarkov.Server.Core.Models.Eft.Common.Location`) per map (`Bigmap`, `Woods`, `Factory4Day`, …) — enumerate via reflection over its properties.
- `Location.Base` → `LocationBase` with `string Id` (map string id like `"bigmap"`), `string Name`, `MongoId IdField` (the mongo `_id`).
- The existing `ExtractTargetStrings(object)` in `SharedQuestsBackend.cs` already unwraps `ListOrT<string>` — reuse it for every `Target` read.
- `C:\SPT\EscapeFromTarkov_Data\Managed\UnityEngine.InputLegacyModule.dll` exists (needed for `UnityEngine.Input` in the client).

---

### Task 1: OverviewBuilder (pure logic) + unit tests

**Files:**
- Create: `Server/OverviewBuilder.cs`
- Modify: `Server.Tests/SharedQuests.Tests.csproj` (add file link)
- Test: `Server.Tests/OverviewBuilderTests.cs`

**Interfaces:**
- Consumes: `ParsedProfile` from `Server/ProfileParser.cs` (`string Nickname`, `Dictionary<string, int> QuestStatusByQid`).
- Produces (Task 2 depends on these exact signatures):
  - `OverviewResponse OverviewBuilder.Build(IReadOnlyList<QuestMeta> quests, IReadOnlyList<ParsedProfile> profiles, IReadOnlyDictionary<string, string> locationIdToMapId)`
  - `List<string> OverviewBuilder.DeriveMaps(QuestMeta quest, IReadOnlyDictionary<string, string> locationIdToMapId)`
  - DTOs `QuestMeta`, `OverviewProfileStatus`, `OverviewQuest`, `OverviewResponse` exactly as defined below.
- Produces (Task 4/5 client DTOs must mirror the serialized shape): `OverviewResponse { Profiles: List<string>, Quests: List<OverviewQuest> }`, `OverviewQuest { Id, Name, Trader, Maps, Statuses }`, `OverviewProfileStatus { Status: int, LockedReason: string? }`.

- [ ] **Step 1: Add the OverviewBuilder file link to the test project**

In `Server.Tests/SharedQuests.Tests.csproj`, extend the existing `<ItemGroup>` with the compile links:

```xml
  <ItemGroup>
    <Compile Include="..\Server\ProfileParser.cs" Link="ProfileParser.cs" />
    <Compile Include="..\Server\OverviewBuilder.cs" Link="OverviewBuilder.cs" />
  </ItemGroup>
```

- [ ] **Step 2: Write failing tests for map derivation**

Create `Server.Tests/OverviewBuilderTests.cs`:

```csharp
using Xunit;

namespace SharedQuests.Tests;

public class OverviewBuilderTests
{
    private static readonly Dictionary<string, string> LocMap = new()
    {
        ["56f40101d2720b2a4d8b45d6"] = "bigmap",
        ["55f2d3fd4bdc2d5f408b4567"] = "factory4_day",
        ["59fc81d786f774390775787e"] = "factory4_night",
        ["5704e3c2d2720bac5b8b4567"] = "Woods",
    };

    private static QuestMeta Quest(string id = "q1", string name = "Test Quest",
        string? locationId = null, List<string>? condLocs = null, List<string>? prereqs = null,
        string? traderId = null)
        => new()
        {
            Id = id, Name = name, TraderId = traderId, LocationId = locationId,
            ConditionLocationIds = condLocs ?? [], PrereqQuestIds = prereqs ?? [],
        };

    private static ParsedProfile Profile(string nick, params (string qid, int status)[] quests)
        => new()
        {
            Nickname = nick,
            QuestStatusByQid = quests.ToDictionary(q => q.qid, q => q.status),
        };

    // --- DeriveMaps ---

    [Fact]
    public void DeriveMaps_SpecificLocationId_ResolvesToCanonicalMap()
    {
        var maps = OverviewBuilder.DeriveMaps(Quest(locationId: "56f40101d2720b2a4d8b45d6"), LocMap);
        Assert.Equal(["bigmap"], maps);
    }

    [Fact]
    public void DeriveMaps_UnresolvableLocation_FallsBackToConditionLocations()
    {
        // "any" marker id is not in the location dict -> scan condition locations
        var q = Quest(locationId: "5af5e9f286f7746c3d532f18",
            condLocs: ["bigmap", "Woods", "bigmap"]);
        var maps = OverviewBuilder.DeriveMaps(q, LocMap);
        Assert.Equal(["bigmap", "woods"], maps); // lowercased, deduped, sorted
    }

    [Fact]
    public void DeriveMaps_FactoryDayAndNight_MergeToFactory()
    {
        var q = Quest(condLocs: ["factory4_day", "factory4_night"]);
        Assert.Equal(["factory"], OverviewBuilder.DeriveMaps(q, LocMap));
    }

    [Fact]
    public void DeriveMaps_ConditionLocationThatIsMongoId_ResolvedThroughDict()
    {
        var q = Quest(condLocs: ["5704e3c2d2720bac5b8b4567"]);
        Assert.Equal(["woods"], OverviewBuilder.DeriveMaps(q, LocMap));
    }

    [Fact]
    public void DeriveMaps_NothingDerivable_ReturnsEmpty()
    {
        Assert.Empty(OverviewBuilder.DeriveMaps(Quest(locationId: "any"), LocMap));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Server.Tests/SharedQuests.Tests.csproj --nologo`
Expected: compile FAILURE — `QuestMeta` / `OverviewBuilder` do not exist.

- [ ] **Step 4: Create `Server/OverviewBuilder.cs` with DTOs and DeriveMaps**

```csharp
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
}
```

- [ ] **Step 5: Run tests to verify DeriveMaps passes**

Run: `dotnet test Server.Tests/SharedQuests.Tests.csproj --nologo`
Expected: PASS (5 new tests + existing ProfileParser tests).

- [ ] **Step 6: Write failing tests for Build (relevance filter, statuses, locked reasons, trader)**

Append to `Server.Tests/OverviewBuilderTests.cs` inside the class:

```csharp
    // --- Build ---

    [Fact]
    public void Build_QuestActiveForNobody_IsOmitted()
    {
        var quests = new List<QuestMeta> { Quest("q1"), Quest("q2", "Active One") };
        var profiles = new List<ParsedProfile>
        {
            Profile("Alice", ("q1", 4), ("q2", 2)), // q1 done, q2 started
            Profile("Bob", ("q1", 0), ("q2", 0)),
        };

        var resp = OverviewBuilder.Build(quests, profiles, LocMap);

        Assert.Equal(["Alice", "Bob"], resp.Profiles);
        var quest = Assert.Single(resp.Quests);
        Assert.Equal("q2", quest.Id);
    }

    [Fact]
    public void Build_IncludedQuest_HasStatusForEveryProfile_MissingDefaultsToLocked()
    {
        var quests = new List<QuestMeta> { Quest("q1") };
        var profiles = new List<ParsedProfile>
        {
            Profile("Alice", ("q1", 3)),
            Profile("Bob"), // no entry for q1
        };

        var resp = OverviewBuilder.Build(quests, profiles, LocMap);

        var statuses = resp.Quests[0].Statuses;
        Assert.Equal(3, statuses["Alice"].Status);
        Assert.Equal(0, statuses["Bob"].Status);
    }

    [Fact]
    public void Build_LockedProfileWithPrereqs_GetsReasonWithPrereqStatusNames()
    {
        var quests = new List<QuestMeta>
        {
            Quest("q1", "Gunsmith - Part 3", prereqs: ["q0"]),
            Quest("q0", "Gunsmith - Part 2"),
        };
        var profiles = new List<ParsedProfile>
        {
            Profile("Alice", ("q1", 2), ("q0", 4)),
            Profile("Carl", ("q1", 0), ("q0", 2)),
        };

        var resp = OverviewBuilder.Build(quests, profiles, LocMap);

        var q1 = resp.Quests.Single(q => q.Id == "q1");
        Assert.Null(q1.Statuses["Alice"].LockedReason);
        Assert.Equal("Gunsmith - Part 2 (Started)", q1.Statuses["Carl"].LockedReason);
    }

    [Fact]
    public void Build_UnknownPrereqId_FallsBackToId()
    {
        var quests = new List<QuestMeta> { Quest("q1", prereqs: ["missing"]) };
        var profiles = new List<ParsedProfile>
        {
            Profile("Alice", ("q1", 1)),
            Profile("Bob", ("q1", 0)),
        };

        var resp = OverviewBuilder.Build(quests, profiles, LocMap);

        Assert.Equal("missing (Locked)", resp.Quests[0].Statuses["Bob"].LockedReason);
    }

    [Fact]
    public void Build_TraderIdResolved_UnknownTraderIsEmpty()
    {
        var quests = new List<QuestMeta>
        {
            Quest("q1", traderId: "5a7c2eca46aef81a7ca2145d"),
            Quest("q2", traderId: "unknown-id"),
        };
        var profiles = new List<ParsedProfile> { Profile("Alice", ("q1", 2), ("q2", 2)) };

        var resp = OverviewBuilder.Build(quests, profiles, LocMap);

        Assert.Equal("Mechanic", resp.Quests.Single(q => q.Id == "q1").Trader);
        Assert.Equal("", resp.Quests.Single(q => q.Id == "q2").Trader);
    }

    [Fact]
    public void Build_QuestsSortedByName()
    {
        var quests = new List<QuestMeta> { Quest("q1", "Zebra"), Quest("q2", "Alpha") };
        var profiles = new List<ParsedProfile> { Profile("Alice", ("q1", 2), ("q2", 2)) };

        var resp = OverviewBuilder.Build(quests, profiles, LocMap);

        Assert.Equal(["Alpha", "Zebra"], resp.Quests.Select(q => q.Name).ToList());
    }
```

- [ ] **Step 7: Run tests to verify the new ones fail**

Run: `dotnet test Server.Tests/SharedQuests.Tests.csproj --nologo`
Expected: compile FAILURE — `OverviewBuilder.Build` not defined.

- [ ] **Step 8: Implement Build**

Add to `OverviewBuilder` (below `DeriveMaps`):

```csharp
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
            var statuses = profiles.ToDictionary(
                p => p.Nickname,
                p => p.QuestStatusByQid.TryGetValue(quest.Id, out var s) ? s : 0);

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
            Profiles = profiles.Select(p => p.Nickname).ToList(),
            Quests = result,
        };
    }

    private static string StatusName(int status) => status switch
    {
        0 => "Locked", 1 => "Available", 2 => "Started", 3 => "Ready",
        4 => "Done", 5 => "Failed", 6 => "Failed (Retry)", 7 => "Failed",
        8 => "Expired", 9 => "Timed", _ => "Unknown",
    };
```

- [ ] **Step 9: Run all tests to verify they pass**

Run: `dotnet test Server.Tests/SharedQuests.Tests.csproj --nologo`
Expected: PASS, 0 failures.

- [ ] **Step 10: Commit**

```bash
git add Server/OverviewBuilder.cs Server.Tests/OverviewBuilderTests.cs Server.Tests/SharedQuests.Tests.csproj
git commit -m "Add OverviewBuilder: pure quest-overview payload assembly with tests"
```

---

### Task 2: Server endpoint `/sharedquests/overview`

**Files:**
- Modify: `Server/SharedQuestsBackend.cs`

**Interfaces:**
- Consumes: `OverviewBuilder.Build(...)`, `QuestMeta`, `OverviewResponse` from Task 1; existing `ExtractTargetStrings`, `ProfileParser.Parse`, `GetLockedReason` machinery.
- Produces: HTTP GET `/sharedquests/overview` returning `OverviewResponse` serialized by SPT's `JsonUtil` (Task 5's client deserializes it with Newtonsoft, which matches property names case-insensitively — client DTO property names must equal the server DTO names).

All edits are in `Server/SharedQuestsBackend.cs`.

- [ ] **Step 1: Add DatabaseService dependency and new caches**

Add to the usings:

```csharp
using SPTarkov.Server.Core.Services;
```

Change the `SharedQuestsServer` primary constructor (currently `logger, router, questHelper`) to:

```csharp
public class SharedQuestsServer(
    ISptLogger<SharedQuestsServer> logger,
    SharedQuestsRouter router,
    QuestHelper questHelper,
    DatabaseService databaseService) : IOnLoad
```

Replace the `_questPrerequisites` field declaration with:

```csharp
    // Cache quest prerequisites (questId -> list of prerequisite quest names)
    private Dictionary<string, List<string>> _questPrerequisites = new();

    // SPT-free quest metadata for the overview endpoint, built once at load
    private List<QuestMeta> _questMetas = new();

    // location mongo id -> map string id ("bigmap"), from the locations DB
    private Dictionary<string, string> _locationIdToMapId = new();
```

- [ ] **Step 2: Replace BuildPrerequisiteCache with a combined quest-meta cache builder**

Replace the entire `BuildPrerequisiteCache()` method with:

```csharp
    /// <summary>
    /// One pass over quest templates: builds the SPT-free QuestMeta list for the
    /// overview endpoint and derives the legacy name-based prerequisite cache from it.
    /// </summary>
    private void BuildQuestMetaCache()
    {
        try
        {
            var allQuests = questHelper.GetQuestsFromDb();
            var questNameById = allQuests.ToDictionary(q => q.Id.ToString(), q => q.QuestName ?? q.Name ?? "Unknown");

            foreach (var quest in allQuests)
            {
                var prereqIds = new List<string>();
                if (quest.Conditions?.AvailableForStart != null)
                {
                    foreach (var condition in quest.Conditions.AvailableForStart)
                    {
                        if (condition.ConditionType == "Quest" && condition.Target != null)
                        {
                            prereqIds.AddRange(ExtractTargetStrings(condition.Target));
                        }
                    }
                }

                var conditionLocationIds = new List<string>();
                if (quest.Conditions?.AvailableForFinish != null)
                {
                    foreach (var condition in quest.Conditions.AvailableForFinish)
                    {
                        if (condition.Counter?.Conditions == null) continue;
                        foreach (var sub in condition.Counter.Conditions)
                        {
                            if (sub.ConditionType == "Location" && sub.Target != null)
                            {
                                conditionLocationIds.AddRange(ExtractTargetStrings(sub.Target));
                            }
                        }
                    }
                }

                _questMetas.Add(new QuestMeta
                {
                    Id = quest.Id.ToString(),
                    Name = questNameById[quest.Id.ToString()],
                    TraderId = quest.TraderId.ToString(),
                    LocationId = quest.Location,
                    ConditionLocationIds = conditionLocationIds.Distinct().ToList(),
                    PrereqQuestIds = prereqIds.Distinct().ToList(),
                });
            }

            // Legacy cache for /sharedquests/statuses locked reasons
            foreach (var meta in _questMetas)
            {
                if (meta.PrereqQuestIds.Count == 0) continue;
                _questPrerequisites[meta.Id] = meta.PrereqQuestIds
                    .Select(id => questNameById.TryGetValue(id, out var n) ? n : id)
                    .ToList();
            }

            logger.Info($"[SharedQuests] Built quest meta cache for {_questMetas.Count} quests ({_questPrerequisites.Count} with prerequisites)");
        }
        catch (Exception ex)
        {
            logger.Error($"[SharedQuests] Error building quest meta cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Build location mongo-id -> map string id from the locations DB, so no map
    /// ids are hardcoded. Enumerates the typed Location properties by reflection.
    /// </summary>
    private void BuildLocationMapCache()
    {
        try
        {
            var locations = databaseService.GetLocations();
            foreach (var prop in locations.GetType().GetProperties())
            {
                if (prop.GetValue(locations) is not SPTarkov.Server.Core.Models.Eft.Common.Location location) continue;
                var locationBase = location.Base;
                if (locationBase?.Id == null) continue;
                _locationIdToMapId[locationBase.IdField.ToString()] = locationBase.Id;
            }
            logger.Info($"[SharedQuests] Built location map cache with {_locationIdToMapId.Count} locations");
        }
        catch (Exception ex)
        {
            logger.Error($"[SharedQuests] Error building location cache: {ex.Message}");
        }
    }
```

- [ ] **Step 3: Call both cache builders from OnLoad**

In `OnLoad()`, replace the line `BuildPrerequisiteCache();` with:

```csharp
        // Build quest metadata and location caches
        BuildQuestMetaCache();
        BuildLocationMapCache();
```

And update the endpoint log line to mention both endpoints:

```csharp
        logger.Info("[SharedQuests] Endpoints available: /sharedquests/statuses, /sharedquests/overview");
```

- [ ] **Step 4: Add GetOverview to SharedQuestsServer**

Add after `GetFreshQuestStatuses()`:

```csharp
    /// <summary>
    /// Assemble the overview payload: fresh profiles from disk + cached quest metadata.
    /// </summary>
    public OverviewResponse GetOverview()
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
            logger.Error($"[SharedQuests] Error reading profiles for overview: {ex.Message}");
        }

        return OverviewBuilder.Build(_questMetas, profiles, _locationIdToMapId);
    }
```

- [ ] **Step 5: Register the route**

In `SharedQuestsRouter.GetCustomRoutes()`, add a second `RouteAction`:

```csharp
    private static List<RouteAction> GetCustomRoutes()
    {
        return
        [
            new RouteAction(
                "/sharedquests/statuses",
                static async (url, info, sessionId, output) => await HandleGetStatuses(sessionId)
            ),
            new RouteAction(
                "/sharedquests/overview",
                static async (url, info, sessionId, output) => await HandleGetOverview()
            )
        ];
    }
```

And add the handler next to `HandleGetStatuses`:

```csharp
    /// <summary>
    /// Returns the map-grouped overview payload - reads profiles fresh from disk
    /// </summary>
    private static ValueTask<string> HandleGetOverview()
    {
        try
        {
            var overview = _server?.GetOverview()
                ?? new OverviewResponse { Profiles = [], Quests = [] };
            return new ValueTask<string>(_jsonUtil!.Serialize(overview)!);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SharedQuests] Error getting overview: {ex.Message}");
            return new ValueTask<string>(_httpResponseUtil!.NullResponse());
        }
    }
```

- [ ] **Step 6: Build server and run tests**

Run: `dotnet build Server/SharedQuestsBackend.csproj --nologo`
Expected: Build succeeded, 0 errors. (If `IdField` or the `Location` cast fails to compile, check the actual property via the type in the IDE — but these names were verified against 4.0.5.)

Run: `dotnet test Server.Tests/SharedQuests.Tests.csproj --nologo`
Expected: PASS (server file is not in the test project; this guards ProfileParser/OverviewBuilder regressions).

- [ ] **Step 7: Commit**

```bash
git add Server/SharedQuestsBackend.cs
git commit -m "Add /sharedquests/overview endpoint with quest meta and location caches"
```

---

### Task 3: Client taskbar button + panel skeleton

**Files:**
- Create: `Client/QuestPlannerButton.cs`
- Create: `Client/QuestPlannerPanel.cs`
- Modify: `Client/SharedQuests.csproj` (add `UnityEngine.InputLegacyModule` reference)

**Interfaces:**
- Consumes: `Plugin.LogSource` (client), `EFT.UI.MenuTaskBar`, `EFT.UI.MenuScreen` (Assembly-CSharp).
- Produces: `QuestPlannerPanel.Instance` (singleton `MonoBehaviour`), `QuestPlannerPanel.Toggle()`, `QuestPlannerPanel.Show()`, `QuestPlannerPanel.Hide()`; `QuestPlannerButton.TryInject()`. Task 4 fills in `RefreshContent()` — this task creates it as a stub that renders nothing.

- [ ] **Step 1: Add the input module reference**

In `Client/SharedQuests.csproj`, inside the `<ItemGroup>` of references, add (alphabetical placement next to the other UnityEngine entries):

```xml
    <Reference Include="UnityEngine.InputLegacyModule">
      <HintPath>$(SPTPath)\EscapeFromTarkov_Data\Managed\UnityEngine.InputLegacyModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
```

- [ ] **Step 2: Create the panel skeleton**

Create `Client/QuestPlannerPanel.cs`. This is the LootNet `RaidHistoryDisplay` structure adapted to a centered panel with the mod's tan accent (`#9A8866`), a close button, a column-header row, a scrollable content area, and ESC-to-close. `RefreshContent()` is a stub filled in by the next task.

```csharp
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SharedQuests
{
    /// <summary>
    /// Full-screen overlay showing all profiles' quest statuses grouped by map.
    /// UI is built entirely in code (no asset bundles), LootNet-style.
    /// </summary>
    public class QuestPlannerPanel : MonoBehaviour
    {
        private static QuestPlannerPanel _instance;
        public static QuestPlannerPanel Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("SharedQuestsPlanner");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<QuestPlannerPanel>();
                }
                return _instance;
            }
        }

        public static readonly Color Accent = new Color(0.604f, 0.533f, 0.400f); // #9A8866
        private const float HeaderH = 72f;
        private const float PanelW = 980f;

        private GameObject _root;
        private RectTransform _contentRt;
        private Transform _contentContainer;
        private ScrollRect _scrollRect;
        private TextMeshProUGUI _messageLabel;
        private GameObject _retryButton;
        private bool _visible;

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            BuildUI();
        }

        private void Update()
        {
            if (_visible && Input.GetKeyDown(KeyCode.Escape)) Hide();
        }

        public void Toggle()
        {
            if (_visible) Hide(); else Show();
        }

        public void Show()
        {
            if (_visible) return;
            _root.SetActive(true);
            _visible = true;
            RefreshContent();
            if (_scrollRect != null) _scrollRect.verticalNormalizedPosition = 1f;
        }

        public void Hide()
        {
            if (!_visible) return;
            _visible = false;
            _root.SetActive(false);
        }

        /// <summary>Fetches overview data and rebuilds the rows. Filled in by the data task.</summary>
        private void RefreshContent()
        {
            ShowMessage("Loading...", showRetry: false);
        }

        /// <summary>Show a centered message (loading / error / empty) instead of rows.</summary>
        private void ShowMessage(string text, bool showRetry)
        {
            ClearContent();
            _messageLabel.text = text;
            _messageLabel.gameObject.SetActive(true);
            _retryButton.SetActive(showRetry);
        }

        private void HideMessage()
        {
            _messageLabel.gameObject.SetActive(false);
            _retryButton.SetActive(false);
        }

        private void ClearContent()
        {
            for (int i = _contentContainer.childCount - 1; i >= 0; i--)
                Destroy(_contentContainer.GetChild(i).gameObject);
        }

        private void BuildUI()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;
            gameObject.AddComponent<CanvasScaler>();
            gameObject.AddComponent<GraphicRaycaster>();

            _root = MakeRect("PlannerRoot", transform);
            Stretch(_root.GetComponent<RectTransform>());

            // Dim backdrop; clicking it closes the panel
            var overlay = MakeRect("Overlay", _root.transform);
            Stretch(overlay.GetComponent<RectTransform>());
            overlay.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);
            var overlayBtn = overlay.AddComponent<Button>();
            overlayBtn.transition = Selectable.Transition.None;
            overlayBtn.onClick.AddListener(Hide);

            // Centered panel, 8%..92% of screen height
            var panelGo = MakeRect("Panel", _root.transform);
            var panelRt = panelGo.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.08f);
            panelRt.anchorMax = new Vector2(0.5f, 0.92f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(PanelW, 0f);
            panelGo.AddComponent<Image>().color = new Color(0.06f, 0.06f, 0.07f, 1f);

            // Block backdrop clicks under the panel
            var panelBlock = panelGo.AddComponent<Button>();
            panelBlock.transition = Selectable.Transition.None;

            var topBar = MakeRect("TopBar", panelGo.transform);
            var topBarRt = topBar.GetComponent<RectTransform>();
            topBarRt.anchorMin = new Vector2(0f, 1f);
            topBarRt.anchorMax = new Vector2(1f, 1f);
            topBarRt.pivot = Vector2.up;
            topBarRt.sizeDelta = new Vector2(0f, 3f);
            topBar.AddComponent<Image>().color = Accent;

            var title = MakeTMP("Title", panelGo.transform, 22f, FontStyles.Bold, TextAlignmentOptions.Left);
            title.text = "SHARED QUESTS";
            title.color = Accent;
            title.characterSpacing = 5f;
            SetRect(title.rectTransform,
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(24f, -HeaderH + 14f), offsetMax: new Vector2(-60f, -14f));

            var sub = MakeTMP("Sub", panelGo.transform, 10f, FontStyles.Normal, TextAlignmentOptions.Left);
            sub.text = "QUEST PROGRESS BY MAP  ·  ESC OR CLICK OUTSIDE TO CLOSE";
            sub.color = new Color(0.35f, 0.35f, 0.35f);
            sub.characterSpacing = 2f;
            SetRect(sub.rectTransform,
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(24f, -HeaderH + 2f), offsetMax: new Vector2(-60f, -HeaderH + 18f));

            // Close button (✕)
            var closeGo = MakeRect("Close", panelGo.transform);
            var closeRt = closeGo.GetComponent<RectTransform>();
            closeRt.anchorMin = new Vector2(1f, 1f);
            closeRt.anchorMax = new Vector2(1f, 1f);
            closeRt.pivot = new Vector2(1f, 1f);
            closeRt.anchoredPosition = new Vector2(-12f, -12f);
            closeRt.sizeDelta = new Vector2(32f, 32f);
            closeGo.AddComponent<Image>().color = Color.clear;
            var closeLabel = MakeTMP("X", closeGo.transform, 18f, FontStyles.Bold, TextAlignmentOptions.Center);
            Stretch(closeLabel.rectTransform);
            closeLabel.text = "✕";
            closeLabel.color = new Color(0.6f, 0.6f, 0.6f);
            var closeBtn = closeGo.AddComponent<Button>();
            closeBtn.transition = Selectable.Transition.None;
            closeBtn.onClick.AddListener(Hide);

            var divider = MakeRect("Divider", panelGo.transform);
            var dividerRt = divider.GetComponent<RectTransform>();
            dividerRt.anchorMin = new Vector2(0f, 1f);
            dividerRt.anchorMax = new Vector2(1f, 1f);
            dividerRt.pivot = Vector2.up;
            dividerRt.anchoredPosition = new Vector2(0f, -HeaderH);
            dividerRt.sizeDelta = new Vector2(0f, 1f);
            divider.AddComponent<Image>().color = new Color(Accent.r, Accent.g, Accent.b, 0.25f);

            // Scrollable content
            var scrollGo = MakeRect("Scroll", panelGo.transform);
            var scrollRt = scrollGo.GetComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = new Vector2(0f, 12f);
            scrollRt.offsetMax = new Vector2(0f, -(HeaderH + 1f));
            _scrollRect = scrollGo.AddComponent<ScrollRect>();
            _scrollRect.horizontal = false;
            _scrollRect.scrollSensitivity = 30f;

            var viewportGo = MakeRect("Viewport", scrollGo.transform);
            Stretch(viewportGo.GetComponent<RectTransform>());
            viewportGo.AddComponent<RectMask2D>();
            _scrollRect.viewport = viewportGo.GetComponent<RectTransform>();

            var contentGo = MakeRect("Content", viewportGo.transform);
            _contentRt = contentGo.GetComponent<RectTransform>();
            _contentRt.anchorMin = new Vector2(0f, 1f);
            _contentRt.anchorMax = new Vector2(1f, 1f);
            _contentRt.pivot = new Vector2(0.5f, 1f);
            var layout = contentGo.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.padding = new RectOffset(24, 24, 4, 16);
            layout.spacing = 2f;
            var fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _scrollRect.content = _contentRt;
            _contentContainer = contentGo.transform;

            // Centered message + retry (for loading/error/empty states)
            _messageLabel = MakeTMP("Message", panelGo.transform, 14f, FontStyles.Normal, TextAlignmentOptions.Center);
            _messageLabel.color = new Color(0.45f, 0.45f, 0.45f);
            SetRect(_messageLabel.rectTransform,
                anchorMin: new Vector2(0f, 0.45f), anchorMax: new Vector2(1f, 0.6f),
                offsetMin: new Vector2(24f, 0f), offsetMax: new Vector2(-24f, 0f));
            _messageLabel.gameObject.SetActive(false);

            var retryGo = MakeRect("Retry", panelGo.transform);
            var retryRt = retryGo.GetComponent<RectTransform>();
            retryRt.anchorMin = new Vector2(0.5f, 0.38f);
            retryRt.anchorMax = new Vector2(0.5f, 0.38f);
            retryRt.pivot = new Vector2(0.5f, 0.5f);
            retryRt.sizeDelta = new Vector2(120f, 32f);
            retryGo.AddComponent<Image>().color = new Color(Accent.r, Accent.g, Accent.b, 0.2f);
            var retryLabel = MakeTMP("Label", retryGo.transform, 12f, FontStyles.Bold, TextAlignmentOptions.Center);
            Stretch(retryLabel.rectTransform);
            retryLabel.text = "RETRY";
            retryLabel.color = Accent;
            var retryBtn = retryGo.AddComponent<Button>();
            retryBtn.transition = Selectable.Transition.None;
            retryBtn.onClick.AddListener(RefreshContent);
            _retryButton = retryGo;
            _retryButton.SetActive(false);

            _root.SetActive(false);
        }

        // --- small builders (LootNet pattern) ---

        internal static GameObject MakeRect(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            return go;
        }

        internal static TextMeshProUGUI MakeTMP(string name, Transform parent,
            float size, FontStyles style, TextAlignmentOptions align)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = align;
            t.richText = true;
            return t;
        }

        internal static void SetRect(RectTransform rt,
            Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin; rt.offsetMax = offsetMax;
        }

        internal static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero; rt.anchoredPosition = Vector2.zero;
        }
    }
}
```

Note: `RefreshContent` must stay a zero-arg `void` method (it is wired to the Retry button's `onClick`).

- [ ] **Step 3: Create the taskbar button + menu patch**

Create `Client/QuestPlannerButton.cs` — LootNet's `RaidHistoryMenuButton` pattern (text-only label, no icon) plus the `MenuScreen.Awake` postfix that injects one frame later. The patch uses the attribute style already used in `SharedQuests.cs` so `harmony.PatchAll()` picks it up:

```csharp
using EFT.UI;
using HarmonyLib;
using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SharedQuests
{
    /// <summary>
    /// Injects a "QUESTS" button into the main menu taskbar that toggles the planner panel.
    /// </summary>
    internal static class QuestPlannerButton
    {
        private static GameObject _button;

        public static void TryInject()
        {
            if (_button) return;

            var taskBar = UnityEngine.Object.FindObjectOfType<MenuTaskBar>(true);
            if (taskBar == null)
            {
                Plugin.LogSource.LogWarning("SharedQuests: MenuTaskBar not found in scene");
                return;
            }

            // The nav row is the HorizontalLayoutGroup with the most children
            HorizontalLayoutGroup best = null;
            int bestCount = 0;
            foreach (var hlg in taskBar.GetComponentsInChildren<HorizontalLayoutGroup>(true))
            {
                if (hlg.transform.childCount > bestCount)
                {
                    bestCount = hlg.transform.childCount;
                    best = hlg;
                }
            }
            if (best == null)
            {
                Plugin.LogSource.LogWarning("SharedQuests: Could not find nav HorizontalLayoutGroup in MenuTaskBar");
                return;
            }

            _button = BuildNavButton(best.transform);
        }

        private static GameObject BuildNavButton(Transform parent)
        {
            var siblingLabel = parent.GetComponentInChildren<TextMeshProUGUI>(true);
            float fontSize = siblingLabel != null ? siblingLabel.fontSize : 12f;
            Color normalColor = siblingLabel != null ? siblingLabel.color : new Color(0.85f, 0.85f, 0.85f);
            float charSpacing = siblingLabel != null ? siblingLabel.characterSpacing : 2f;

            var go = new GameObject("SharedQuestsPlannerButton");
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();

            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 110f;
            le.flexibleWidth = 0f;

            var bg = go.AddComponent<Image>();
            bg.color = Color.clear;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(8f, 0f);
            labelRt.offsetMax = new Vector2(-4f, 0f);
            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = "QUESTS";
            label.fontSize = fontSize;
            label.fontStyle = FontStyles.Bold;
            label.color = normalColor;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.characterSpacing = charSpacing;
            label.overflowMode = TextOverflowModes.Ellipsis;
            if (siblingLabel != null && siblingLabel.font != null) label.font = siblingLabel.font;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => QuestPlannerPanel.Instance.Toggle());

            Color hover = QuestPlannerPanel.Accent;
            Color dim = new Color(normalColor.r * 0.6f, normalColor.g * 0.6f, normalColor.b * 0.6f);
            var et = go.AddComponent<EventTrigger>();
            AddTrigger(et, EventTriggerType.PointerEnter, _ => label.color = hover);
            AddTrigger(et, EventTriggerType.PointerExit, _ => label.color = normalColor);
            AddTrigger(et, EventTriggerType.PointerDown, _ => label.color = dim);
            AddTrigger(et, EventTriggerType.PointerUp, _ => label.color = hover);

            go.SetActive(true);
            return go;
        }

        private static void AddTrigger(EventTrigger et, EventTriggerType type,
            UnityEngine.Events.UnityAction<BaseEventData> action)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(action);
            et.triggers.Add(entry);
        }
    }

    /// <summary>
    /// Inject the taskbar button one frame after the main menu awakes.
    /// </summary>
    [HarmonyPatch(typeof(MenuScreen), "Awake")]
    internal class MenuScreenAwakePatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            QuestPlannerPanel.Instance.StartCoroutine(InjectNextFrame());
        }

        private static IEnumerator InjectNextFrame()
        {
            yield return null;
            try
            {
                QuestPlannerButton.TryInject();
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"SharedQuests: Planner button inject failed: {ex.Message}");
            }
        }
    }
}
```

- [ ] **Step 4: Build the client**

Run: `dotnet build Client/SharedQuests.csproj --nologo`
Expected: Build succeeded, 0 errors. (If `MenuScreen`/`MenuTaskBar` resolve fails, they live in `EFT.UI` in Assembly-CSharp — check the using, not the reference.)

- [ ] **Step 5: Commit**

```bash
git add Client/QuestPlannerButton.cs Client/QuestPlannerPanel.cs Client/SharedQuests.csproj
git commit -m "Add planner panel skeleton and QUESTS taskbar button"
```

---

### Task 4: Panel data rendering (fetch, group by map, rows)

**Files:**
- Modify: `Client/QuestPlannerPanel.cs`

**Interfaces:**
- Consumes: HTTP GET `/sharedquests/overview` via `SPT.Common.Http.RequestHandler.GetJson(string)` (synchronous, returns JSON string); `Settings.IsProfileVisible(string)`, `Settings.UpdateProfileList(IEnumerable<string>)`; `Plugin.GetStatusName(int)`, `Plugin.GetStatusColor(int)` (existing, return display name / hex color string).
- Produces: the completed `RefreshContent()`; DTO classes `OverviewResponse`, `OverviewQuest`, `OverviewProfileStatus` (client copies — property names must match the server DTOs from Task 1: `Profiles`, `Quests`, `Id`, `Name`, `Trader`, `Maps`, `Statuses`, `Status`, `LockedReason`; Newtonsoft matches case-insensitively).

- [ ] **Step 1: Add usings and DTOs**

At the top of `Client/QuestPlannerPanel.cs` add usings:

```csharp
using Newtonsoft.Json;
using SPT.Common.Http;
using System.Linq;
```

Inside the `SharedQuests` namespace (above the `QuestPlannerPanel` class), add the response DTOs:

```csharp
    /// <summary>Overview payload from /sharedquests/overview (mirrors server DTOs).</summary>
    public class OverviewProfileStatus
    {
        public int Status { get; set; }
        public string LockedReason { get; set; }
    }

    public class OverviewQuest
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Trader { get; set; }
        public List<string> Maps { get; set; }
        public Dictionary<string, OverviewProfileStatus> Statuses { get; set; }
    }

    public class OverviewResponse
    {
        public List<string> Profiles { get; set; }
        public List<OverviewQuest> Quests { get; set; }
    }
```

- [ ] **Step 2: Add map display names and row constants to QuestPlannerPanel**

Add fields to the class:

```csharp
        // canonical map id (from server) -> display name
        private static readonly Dictionary<string, string> MapNames = new Dictionary<string, string>
        {
            ["bigmap"] = "CUSTOMS",
            ["factory"] = "FACTORY",
            ["interchange"] = "INTERCHANGE",
            ["laboratory"] = "THE LAB",
            ["lighthouse"] = "LIGHTHOUSE",
            ["rezervbase"] = "RESERVE",
            ["sandbox"] = "GROUND ZERO",
            ["shoreline"] = "SHORELINE",
            ["tarkovstreets"] = "STREETS OF TARKOV",
            ["woods"] = "WOODS",
            ["labyrinth"] = "LABYRINTH",
            ["suburbs"] = "SUBURBS",
            ["terminal"] = "TERMINAL",
            ["town"] = "TOWN",
        };

        private const string AnyMapKey = "__any__";
        private const float PlayerColW = 105f;
        private const float RowH = 26f;
        private const float SectionHeaderH = 34f;

        // map id -> expanded state, persists across refreshes while the game runs
        private readonly Dictionary<string, bool> _sectionExpanded = new Dictionary<string, bool>();
```

- [ ] **Step 3: Replace the RefreshContent stub with fetch + render**

Replace the stub `RefreshContent()` with:

```csharp
        /// <summary>Fetches overview data and rebuilds the rows.</summary>
        private void RefreshContent()
        {
            ShowMessage("Loading...", showRetry: false);

            OverviewResponse data;
            try
            {
                var response = RequestHandler.GetJson("/sharedquests/overview");
                data = JsonConvert.DeserializeObject<OverviewResponse>(response);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"SharedQuests: Error fetching overview: {ex.Message}");
                data = null;
            }

            if (data == null || data.Profiles == null || data.Quests == null)
            {
                ShowMessage("Couldn't reach SharedQuests server", showRetry: true);
                return;
            }

            // Keep the F12 profile checkboxes in sync
            Settings.UpdateProfileList(data.Profiles);

            var visibleProfiles = data.Profiles.Where(Settings.IsProfileVisible).ToList();
            if (visibleProfiles.Count == 0)
            {
                ShowMessage("No profiles selected (check F12 menu)", showRetry: false);
                return;
            }

            // Re-apply relevance for visible profiles only: a quest active solely
            // for excluded profiles is hidden entirely.
            bool IsActive(OverviewQuest q, string profile) =>
                q.Statuses.TryGetValue(profile, out var s) && (s.Status == 1 || s.Status == 2 || s.Status == 3);
            var relevant = data.Quests
                .Where(q => visibleProfiles.Any(p => IsActive(q, p)))
                .ToList();

            if (relevant.Count == 0)
            {
                ShowMessage("No active quests found", showRetry: false);
                return;
            }

            // Group by map (a multi-map quest appears under each map; no maps -> "any map")
            var groups = new Dictionary<string, List<OverviewQuest>>();
            foreach (var quest in relevant)
            {
                var keys = (quest.Maps != null && quest.Maps.Count > 0) ? quest.Maps : new List<string> { AnyMapKey };
                foreach (var key in keys)
                {
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<OverviewQuest>();
                    list.Add(quest);
                }
            }

            // Sort: most players with active quests desc, then quest count desc; "any map" last
            var ordered = groups
                .OrderBy(g => g.Key == AnyMapKey ? 1 : 0)
                .ThenByDescending(g => visibleProfiles.Count(p => g.Value.Any(q => IsActive(q, p))))
                .ThenByDescending(g => g.Value.Count)
                .ToList();

            HideMessage();
            ClearContent();
            BuildHeaderRow(visibleProfiles);
            foreach (var group in ordered)
                BuildMapSection(group.Key, group.Value, visibleProfiles, IsActive);

            LayoutRebuilder.ForceRebuildLayoutImmediate(_contentRt);
        }
```

- [ ] **Step 4: Add the row builders**

Add these methods to `QuestPlannerPanel`:

```csharp
        /// <summary>Sticky-ish first row: blank name column + one profile name per column.</summary>
        private void BuildHeaderRow(List<string> profiles)
        {
            var row = MakeRow("HeaderRow", RowH + 6f);
            AddCell(row.transform, "", flexible: true, 12f, FontStyles.Bold, Color.clear);
            foreach (var profile in profiles)
            {
                var cell = AddCell(row.transform, profile, flexible: false, 12f, FontStyles.Bold,
                    new Color(0.8f, 0.8f, 0.8f));
                cell.overflowMode = TextOverflowModes.Ellipsis;
            }
        }

        private void BuildMapSection(string mapKey, List<OverviewQuest> quests,
            List<string> profiles, Func<OverviewQuest, string, bool> isActive)
        {
            string displayName = mapKey == AnyMapKey
                ? "ANY MAP"
                : (MapNames.TryGetValue(mapKey, out var n) ? n : mapKey.ToUpperInvariant());
            int playerCount = profiles.Count(p => quests.Any(q => isActive(q, p)));

            // "Any map" starts collapsed, map sections start expanded
            if (!_sectionExpanded.TryGetValue(mapKey, out var expanded))
                _sectionExpanded[mapKey] = expanded = mapKey != AnyMapKey;

            // Section header (click to toggle)
            var headerGo = MakeRow($"Section_{mapKey}", SectionHeaderH);
            headerGo.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.04f);
            var headerLabel = MakeTMP("Label", headerGo.transform, 14f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            Stretch(headerLabel.rectTransform);
            headerLabel.rectTransform.offsetMin = new Vector2(8f, 0f);
            string arrow = expanded ? "▼" : "▶";
            string plural = playerCount == 1 ? "player" : "players";
            string questPlural = quests.Count == 1 ? "quest" : "quests";
            headerLabel.text =
                $"<color=#9A8866>{arrow}  {displayName}</color>" +
                $"<color=#666666>   {playerCount} {plural} · {quests.Count} {questPlural}</color>";

            // Rows container so toggling is a single SetActive
            var rowsGo = MakeRect($"Rows_{mapKey}", _contentContainer);
            var rowsLayout = rowsGo.AddComponent<VerticalLayoutGroup>();
            rowsLayout.childControlWidth = true;
            rowsLayout.childControlHeight = true;
            rowsLayout.childForceExpandWidth = true;
            rowsLayout.childForceExpandHeight = false;
            rowsLayout.spacing = 1f;
            rowsGo.SetActive(expanded);

            var headerBtn = headerGo.AddComponent<Button>();
            headerBtn.transition = Selectable.Transition.None;
            headerBtn.onClick.AddListener(() =>
            {
                bool now = !_sectionExpanded[mapKey];
                _sectionExpanded[mapKey] = now;
                rowsGo.SetActive(now);
                headerLabel.text = headerLabel.text.Replace(now ? "▶" : "▼", now ? "▼" : "▶");
                LayoutRebuilder.ForceRebuildLayoutImmediate(_contentRt);
            });

            foreach (var quest in quests.OrderBy(q => q.Name, StringComparer.Ordinal))
                BuildQuestRow(rowsGo.transform, quest, profiles);
        }

        private void BuildQuestRow(Transform parent, OverviewQuest quest, List<string> profiles)
        {
            var row = MakeRow("Quest", RowH, parent);

            string traderSuffix = string.IsNullOrEmpty(quest.Trader) ? "" : $"  <color=#555555>{quest.Trader}</color>";
            var nameCell = AddCell(row.transform, $"<color=#CCCCCC>{quest.Name}</color>{traderSuffix}",
                flexible: true, 12f, FontStyles.Normal, Color.white);
            nameCell.overflowMode = TextOverflowModes.Ellipsis;

            foreach (var profile in profiles)
            {
                OverviewProfileStatus info = null;
                if (quest.Statuses != null) quest.Statuses.TryGetValue(profile, out info);
                int status = info != null ? info.Status : 0;
                // Locked with no known blocker = quest just isn't relevant to this profile yet
                bool notRelevant = status == 0 && (info == null || string.IsNullOrEmpty(info.LockedReason));
                var cell = AddCell(row.transform, "", flexible: false, 11f, FontStyles.Bold, Color.white);
                cell.text = notRelevant
                    ? "<color=#555555>–</color>"
                    : $"<color={Plugin.GetStatusColor(status)}>{Plugin.GetStatusName(status)}</color>";
            }

            // One indented sub-row per blocked profile with a known reason
            foreach (var profile in profiles)
            {
                if (quest.Statuses == null || !quest.Statuses.TryGetValue(profile, out var info)) continue;
                if (info.Status != 0 || string.IsNullOrEmpty(info.LockedReason)) continue;
                var subRow = MakeRow("Blocked", RowH - 6f, parent);
                var subLabel = AddCell(subRow.transform,
                    $"<color=#666666>└ {profile} needs: {info.LockedReason}</color>",
                    flexible: true, 10f, FontStyles.Normal, Color.white);
                subLabel.rectTransform.offsetMin = new Vector2(24f, 0f);
                subLabel.overflowMode = TextOverflowModes.Ellipsis;
            }
        }

        /// <summary>A fixed-height row with a horizontal layout, parented to content by default.</summary>
        private GameObject MakeRow(string name, float height, Transform parent = null)
        {
            var go = MakeRect(name, parent ?? _contentContainer);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleHeight = 0f;
            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            layout.spacing = 4f;
            return go;
        }

        private TextMeshProUGUI AddCell(Transform row, string text, bool flexible,
            float fontSize, FontStyles style, Color color)
        {
            var label = MakeTMP("Cell", row, fontSize, style,
                flexible ? TextAlignmentOptions.MidlineLeft : TextAlignmentOptions.Midline);
            label.text = text;
            label.color = color;
            var le = label.gameObject.AddComponent<LayoutElement>();
            if (flexible) { le.flexibleWidth = 1f; le.minWidth = 200f; }
            else { le.preferredWidth = PlayerColW; le.flexibleWidth = 0f; }
            return label;
        }
```

- [ ] **Step 5: Build the client**

Run: `dotnet build Client/SharedQuests.csproj --nologo`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add Client/QuestPlannerPanel.cs
git commit -m "Render map-grouped quest overview in planner panel"
```

---

### Task 5: Docs + release artifacts + manual verification handoff

**Files:**
- Modify: `README.md`

**Interfaces:** none — documentation and verification.

- [ ] **Step 1: Add the feature to README**

In `README.md`, add to the `## Features` list (after the "Real-time Quest Status" bullet):

```markdown
- **Quest Planner** - "QUESTS" button in the main menu opens an overlay showing everyone's quests grouped by map, sorted by overlap, with blocked players and their missing prerequisites - pick the best map for the group at a glance
```

- [ ] **Step 2: Build both dists**

Run: `dotnet build Server/SharedQuestsBackend.csproj --nologo && dotnet build Client/SharedQuests.csproj --nologo`
Expected: both succeed; DLLs land in `dist/` and are copied to `C:\SPT` by the `CopyToSPT` targets.

- [ ] **Step 3: Run full test suite**

Run: `dotnet test Server.Tests/SharedQuests.Tests.csproj --nologo`
Expected: PASS, 0 failures.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "Document quest planner feature"
```

- [ ] **Step 5: Manual in-game verification (requires the user)**

Cannot be automated — report this checklist to the user:

1. Start SPT server; log should show `Built quest meta cache for N quests` and `Built location map cache with N locations`.
2. `curl http://127.0.0.1:6969/sharedquests/overview` (or the configured port) returns JSON with `Profiles` and `Quests`, quests have plausible `Maps`.
3. Launch the game → main menu shows a `QUESTS` taskbar button.
4. Click it → panel opens; maps sorted by overlap; statuses match the Tasks screen; blocked players show "needs: X (Status)" sub-rows.
5. ESC, ✕, and clicking outside all close the panel; section headers collapse/expand.
6. F12 → hide a profile → reopen panel → column gone, groups re-sorted.
7. Stop the server mid-session → open panel → "Couldn't reach SharedQuests server" + working RETRY.
