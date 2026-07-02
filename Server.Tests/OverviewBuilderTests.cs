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
}
