using Xunit;

namespace SharedQuests.Tests;

/// <summary>
/// Tests against real profile snapshots from our game server (mocks/profiles).
/// Every expectation below was extracted from the actual JSON, so these pin the
/// mod's behavior on production-shaped data, quirks included.
/// </summary>
public class RealProfileTests
{
    // The screenshot quest: "hand over BNTI Module-3M / TOZ-106", two handover conditions.
    private const string HandoverQuest = "596b36c586f77450d6045ad2";
    private const string BntiCond = "597867e986f7741b265c6bd3";
    private const string TozCond = "5ab8d44c86f7745b2325bd0c";

    // In progress (Started) for Marklar/KllpDreams/clinicallylazy; absent for SoloLar/bear.
    private const string StartedQuest = "67f3ea581cd4c15d3d040305";
    private const string StartedCond = "67f3fb467def2176367b6a3d"; // Marklar 17, KllpDreams 24, clinicallylazy 23

    // Mixed statuses: Success for KllpDreams/clinicallylazy, Started for Marklar/SoloLar, absent for bear.
    private const string MixedQuest = "59674cd986f7744ab26e32f2";
    private const string MixedCond = "5cb31b6188a450159d330a18"; // Marklar 14, KllpDreams 17, clinicallylazy 16

    private static readonly Dictionary<string, ParsedProfile> ByNick = LoadAll();

    private static Dictionary<string, ParsedProfile> LoadAll()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "mocks", "profiles");
        var result = new Dictionary<string, ParsedProfile>();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var parsed = ProfileParser.Parse(File.ReadAllText(file));
            Assert.NotNull(parsed);
            result[parsed!.Nickname] = parsed;
        }
        return result;
    }

    private static List<ParsedProfile> AllProfiles => ByNick.Values.ToList();

    // --- ProfileParser ---

    [Fact]
    public void Parse_AllFiles_YieldFiveDistinctNicknames()
    {
        Assert.Equal(
            new[] { "KllpDreams", "Marklar", "SoloLar", "bear", "clinicallylazy" },
            ByNick.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    // clinicallylazy has 70 quest entries but two qids appear twice (repeatables) -> 68 distinct.
    [Theory]
    [InlineData("SoloLar", 35, 152)]
    [InlineData("Marklar", 62, 219)]
    [InlineData("KllpDreams", 59, 219)]
    [InlineData("clinicallylazy", 68, 225)]
    [InlineData("bear", 11, 144)]
    public void Parse_QuestAndCounterCounts(string nick, int quests, int counters)
    {
        Assert.Equal(quests, ByNick[nick].QuestStatusByQid.Count);
        Assert.Equal(counters, ByNick[nick].CounterByConditionId.Count);
    }

    // Marklar's profile contains JSON keys differing only by case
    // (needFuelForAllProductionTime vs NeedFuelForAllProductionTime) — must not trip the parser.
    [Fact]
    public void Parse_MarklarCaseVaryingKeys_ParsesFine()
    {
        Assert.Equal(62, ByNick["Marklar"].QuestStatusByQid.Count);
    }

    // Duplicate qid entries within one profile: last write wins, no throw.
    [Theory]
    [InlineData("6a473e033a497b0aa81c4d84")]
    [InlineData("6a47533e40d4d4245878528b")]
    public void Parse_DuplicateQuestEntries_LastWinsNoThrow(string qid)
    {
        Assert.Equal(4, ByNick["clinicallylazy"].QuestStatusByQid[qid]);
    }

    [Fact]
    public void Parse_SoloLar_KnownQuestAndCounter()
    {
        var solo = ByNick["SoloLar"];
        Assert.Equal(4, solo.QuestStatusByQid["657315df034d76585f032e01"]);
        Assert.Contains("657334311dbb8b7569bb83c4", solo.CompletedConditionsByQid["657315df034d76585f032e01"]);
        Assert.Equal(("65141dd6303df252af1c72c9", 8), solo.CounterByConditionId["65141df0e69594cf853a40b9"]);
    }

    // SoloLar's profile is the anomaly source: 24 Success quests, only 2 with completedConditions.
    [Fact]
    public void Parse_SoloLar_MostSuccessQuestsLackCompletedConditions()
    {
        var solo = ByNick["SoloLar"];
        Assert.Equal(24, solo.QuestStatusByQid.Values.Count(s => s == 4));
        Assert.Equal(2, solo.CompletedConditionsByQid.Count);
    }

    // --- QuestDetailBuilder: the screenshot quest ---

    private static QuestDetailMeta HandoverMeta() => new()
    {
        Id = HandoverQuest,
        Name = "Handover Quest",
        Trader = "Prapor",
        Maps = [],
        Description = "",
        Objectives =
        [
            new ObjectiveMeta { ConditionId = BntiCond, Text = "Hand over BNTI Module-3M", Target = 1 },
            new ObjectiveMeta { ConditionId = TozCond, Text = "Hand over TOZ-106", Target = 1 },
        ],
        Prereqs = [],
        Rewards = [],
    };

    // SoloLar: counters only (was the orange 1/1). Marklar: one cc + one counter (mixed row).
    // KllpDreams/clinicallylazy: completedConditions. All are Success, so everyone is done.
    [Theory]
    [InlineData("SoloLar")]
    [InlineData("Marklar")]
    [InlineData("KllpDreams")]
    [InlineData("clinicallylazy")]
    public void Detail_ScreenshotQuest_AllCompletersShowDone(string nick)
    {
        var detail = QuestDetailBuilder.Build(HandoverMeta(), AllProfiles);
        foreach (var objective in detail.Objectives)
        {
            Assert.True(objective.Progress[nick].Done, $"{nick} should be done: {objective.Text}");
            Assert.Null(objective.Progress[nick].Count);
        }
    }

    [Fact]
    public void Detail_ScreenshotQuest_BearHasNoProgress()
    {
        var detail = QuestDetailBuilder.Build(HandoverMeta(), AllProfiles);
        foreach (var objective in detail.Objectives)
        {
            Assert.False(objective.Progress["bear"].Done);
            Assert.Null(objective.Progress["bear"].Count);
        }
    }

    // --- QuestDetailBuilder: in-progress counters ---

    // Real-data quirk this test pins down: counters keep incrementing past the objective's
    // target (Marklar 17, KllpDreams 24, clinicallylazy 23 for the SAME fulfilled objective),
    // so completedConditions must win over the counter while the quest is still Started —
    // done with no count, even when the meta target exceeds the counter value.
    [Fact]
    public void Detail_StartedQuest_CompletedConditionWinsOverLiveCounter()
    {
        var meta = new QuestDetailMeta
        {
            Id = StartedQuest, Name = "Started Quest", Trader = "", Maps = [], Description = "",
            Objectives = [new ObjectiveMeta { ConditionId = StartedCond, Text = "Do the thing", Target = 25 }],
            Prereqs = [], Rewards = [],
        };
        var progress = QuestDetailBuilder.Build(meta, AllProfiles).Objectives[0].Progress;

        Assert.All(new[] { "Marklar", "KllpDreams", "clinicallylazy" }, n =>
        {
            Assert.True(progress[n].Done);
            Assert.Null(progress[n].Count);
        });
        // Not on the quest at all: no counter, not done.
        Assert.All(new[] { "SoloLar", "bear" }, n =>
        {
            Assert.False(progress[n].Done);
            Assert.Null(progress[n].Count);
        });
    }

    // Success profiles are done regardless of counters; Started profiles show live counts.
    [Fact]
    public void Detail_MixedQuest_SuccessAndStartedCoexist()
    {
        var meta = new QuestDetailMeta
        {
            Id = MixedQuest, Name = "Mixed Quest", Trader = "", Maps = [], Description = "",
            Objectives = [new ObjectiveMeta { ConditionId = MixedCond, Text = "Eliminate targets", Target = 15 }],
            Prereqs = [], Rewards = [],
        };
        var progress = QuestDetailBuilder.Build(meta, AllProfiles).Objectives[0].Progress;

        Assert.True(progress["KllpDreams"].Done);      // Success (counter 17 also >= 15)
        Assert.True(progress["clinicallylazy"].Done);  // Success
        Assert.False(progress["Marklar"].Done);        // Started, 14/15
        Assert.Equal(14, progress["Marklar"].Count);
        Assert.False(progress["SoloLar"].Done);        // Started but no counter for this condition
        Assert.Null(progress["SoloLar"].Count);
        Assert.False(progress["bear"].Done);           // Quest absent
    }

    // --- OverviewBuilder ---

    private static List<QuestMeta> OverviewMetas() =>
    [
        // All five profiles have this at Success (or done) -> excluded from overview.
        new QuestMeta { Id = "657315ddab5a49b71f098853", Name = "All Done Quest", TraderId = "54cb50c76803fa8b248b4571" },
        new QuestMeta { Id = MixedQuest, Name = "Mixed Quest", TraderId = "54cb50c76803fa8b248b4571" },
        new QuestMeta
        {
            Id = StartedQuest, Name = "Started Quest", TraderId = "54cb50c76803fa8b248b4571",
            PrereqQuestIds = [MixedQuest],
        },
    ];

    [Fact]
    public void Overview_ListsAllFiveProfiles()
    {
        var overview = OverviewBuilder.Build(OverviewMetas(), AllProfiles, new Dictionary<string, string>());
        Assert.Equal(5, overview.Profiles.Count);
    }

    [Fact]
    public void Overview_AllSuccessQuestExcluded_ActiveQuestsIncluded()
    {
        var overview = OverviewBuilder.Build(OverviewMetas(), AllProfiles, new Dictionary<string, string>());
        var ids = overview.Quests.Select(q => q.Id).ToList();
        Assert.DoesNotContain("657315ddab5a49b71f098853", ids);
        Assert.Contains(MixedQuest, ids);
        Assert.Contains(StartedQuest, ids);
    }

    [Fact]
    public void Overview_MixedQuest_StatusesPerProfile()
    {
        var overview = OverviewBuilder.Build(OverviewMetas(), AllProfiles, new Dictionary<string, string>());
        var statuses = overview.Quests.Single(q => q.Id == MixedQuest).Statuses;
        Assert.Equal(4, statuses["KllpDreams"].Status);
        Assert.Equal(4, statuses["clinicallylazy"].Status);
        Assert.Equal(2, statuses["Marklar"].Status);
        Assert.Equal(2, statuses["SoloLar"].Status);
        Assert.Equal(0, statuses["bear"].Status); // quest absent from profile -> Locked
    }

    // KllpDreams-only repeatable, ready to turn in (status 3); everyone else lacks it entirely.
    [Fact]
    public void Overview_ReadyQuest_SingleActiveProfileStillIncluded()
    {
        var metas = new List<QuestMeta> { new() { Id = "6a45f8a47abbb525ac94c565", Name = "Kllp Repeatable" } };
        var overview = OverviewBuilder.Build(metas, AllProfiles, new Dictionary<string, string>());
        var statuses = Assert.Single(overview.Quests).Statuses;
        Assert.Equal(3, statuses["KllpDreams"].Status);
        Assert.All(new[] { "SoloLar", "Marklar", "clinicallylazy", "bear" },
            n => Assert.Equal(0, statuses[n].Status));
    }

    [Fact]
    public void Detail_ReadyQuest_DoneViaCompletedConditionBeforeTurnIn()
    {
        var meta = new QuestDetailMeta
        {
            Id = "6a45f8a47abbb525ac94c565", Name = "Kllp Repeatable", Trader = "", Maps = [], Description = "",
            Objectives = [new ObjectiveMeta { ConditionId = "6a45f8a47abbb525ac94c569", Text = "Objective", Target = 1 }],
            Prereqs = [], Rewards = [],
        };
        var progress = QuestDetailBuilder.Build(meta, AllProfiles).Objectives[0].Progress;
        Assert.True(progress["KllpDreams"].Done);
        Assert.All(new[] { "SoloLar", "Marklar", "clinicallylazy", "bear" },
            n => Assert.False(progress[n].Done));
    }

    // A quest every profile has unlocked but not yet accepted.
    [Fact]
    public void Overview_AvailableForStartQuest_AllProfilesAtOne()
    {
        var metas = new List<QuestMeta> { new() { Id = "6744ab1def61d56e020b5c56", Name = "Unlocked Quest" } };
        var overview = OverviewBuilder.Build(metas, AllProfiles, new Dictionary<string, string>());
        var statuses = Assert.Single(overview.Quests).Statuses;
        Assert.All(ByNick.Keys, n => Assert.Equal(1, statuses[n].Status));
    }

    // Locked profiles get a reason naming each prerequisite with their own status on it.
    [Fact]
    public void Overview_LockedReason_UsesProfileOwnPrereqStatus()
    {
        var overview = OverviewBuilder.Build(OverviewMetas(), AllProfiles, new Dictionary<string, string>());
        var statuses = overview.Quests.Single(q => q.Id == StartedQuest).Statuses;

        Assert.Equal("Mixed Quest (Locked)", statuses["bear"].LockedReason);     // bear lacks the prereq too
        Assert.Equal("Mixed Quest (Started)", statuses["SoloLar"].LockedReason); // SoloLar has prereq Started
        Assert.Null(statuses["Marklar"].LockedReason);                            // not locked -> no reason
    }
}
