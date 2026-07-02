using Xunit;

namespace SharedQuests.Tests;

public class ProfileParserTests
{
    private const string ValidJson = """
    {
      "characters": { "pmc": {
        "Info": { "Nickname": "Alice" },
        "Quests": [
          { "qid": "q1", "status": 2 },
          { "qid": "q2", "status": "AvailableForFinish" }
        ]
      }}
    }
    """;

    [Fact]
    public void Parse_ValidProfile_ReturnsNicknameAndStatuses()
    {
        var p = ProfileParser.Parse(ValidJson);
        Assert.NotNull(p);
        Assert.Equal("Alice", p!.Nickname);
        Assert.Equal(2, p.QuestStatusByQid["q1"]);
        Assert.Equal(3, p.QuestStatusByQid["q2"]);
    }

    [Fact]
    public void Parse_DuplicateQid_LastWriteWins()
    {
        var json = """
        { "characters": { "pmc": { "Info": { "Nickname": "Bob" },
          "Quests": [ { "qid": "q1", "status": 1 }, { "qid": "q1", "status": 4 } ] } } }
        """;
        var p = ProfileParser.Parse(json);
        Assert.Equal(4, p!.QuestStatusByQid["q1"]);
    }

    [Fact]
    public void Parse_MissingNickname_ReturnsNull()
    {
        var json = """{ "characters": { "pmc": { "Info": {}, "Quests": [] } } }""";
        Assert.Null(ProfileParser.Parse(json));
    }

    [Fact]
    public void Parse_MissingQuests_ReturnsEmptyMap()
    {
        var json = """{ "characters": { "pmc": { "Info": { "Nickname": "Cleo" } } } }""";
        var p = ProfileParser.Parse(json);
        Assert.NotNull(p);
        Assert.Empty(p!.QuestStatusByQid);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsNull()
    {
        Assert.Null(ProfileParser.Parse("not json {"));
    }

    [Fact]
    public void Parse_UnknownStatusName_DefaultsToLocked()
    {
        var json = """
        { "characters": { "pmc": { "Info": { "Nickname": "Dan" },
          "Quests": [ { "qid": "q1", "status": "Bogus" } ] } } }
        """;
        var p = ProfileParser.Parse(json);
        Assert.Equal(0, p!.QuestStatusByQid["q1"]);
    }

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
}
