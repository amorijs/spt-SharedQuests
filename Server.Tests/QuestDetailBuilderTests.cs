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
        Assert.Equal("q0", detail.Prereqs[0].Id);
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
