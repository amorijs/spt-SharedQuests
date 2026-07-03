using System.Text;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Logger;

namespace SharedQuests;

/// <summary>
/// Mod metadata (replaces package.json)
/// </summary>
public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.sharedquests.backend";
    public override string Name { get; init; } = "SharedQuests Backend";
    public override string Author { get; init; } = "SharedQuests";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("2.0.1");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; }
    public override string License { get; init; } = "MIT";
}

/// <summary>
/// Quest status info returned by the API
/// </summary>
public class QuestStatusInfo
{
    public int Status { get; set; }
    public string? LockedReason { get; set; }
}

/// <summary>
/// HTTP Router for real-time quest status endpoint
/// </summary>
[Injectable]
public class SharedQuestsRouter : StaticRouter
{
    private static JsonUtil? _jsonUtil;
    private static HttpResponseUtil? _httpResponseUtil;
    private static SharedQuestsServer? _server;
    private static ISptLogger<SharedQuestsServer>? _logger;

    public SharedQuestsRouter(JsonUtil jsonUtil, HttpResponseUtil httpResponseUtil) 
        : base(jsonUtil, GetCustomRoutes())
    {
        _jsonUtil = jsonUtil;
        _httpResponseUtil = httpResponseUtil;
    }

    public void SetServer(SharedQuestsServer server)
    {
        _server = server;
    }

    public void SetLogger(ISptLogger<SharedQuestsServer> logger)
    {
        _logger = logger;
    }

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

    /// <summary>
    /// Returns current quest statuses - ALWAYS reads fresh from disk
    /// </summary>
    private static ValueTask<string> HandleGetStatuses(MongoId sessionId)
    {
        try
        {
            // Always read fresh data from disk
            var freshData = _server?.GetFreshQuestStatuses();
            
            // Debug: Log status for first quest across all profiles
            if (freshData != null && freshData.Count > 0)
            {
                var firstQuestId = freshData.Values.FirstOrDefault()?.Keys.FirstOrDefault();
                if (firstQuestId != null)
                {
                    var statuses = freshData.Select(kv => 
                    {
                        kv.Value.TryGetValue(firstQuestId, out var info);
                        return $"{kv.Key}={(QuestStatusEnum)(info?.Status ?? 0)}";
                    });
                    _logger?.Debug($"[SharedQuests] Quest {firstQuestId.Substring(0, 12)}... status: {string.Join(", ", statuses)}");
                }
            }
            
            return new ValueTask<string>(_jsonUtil!.Serialize(freshData ?? new Dictionary<string, Dictionary<string, QuestStatusInfo>>())!);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SharedQuests] Error getting statuses: {ex.Message}");
            return new ValueTask<string>(_httpResponseUtil!.NullResponse());
        }
    }

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

}

/// <summary>Dynamic (prefix-match) router for per-quest detail: /sharedquests/quest/&lt;id&gt;.</summary>
[Injectable]
public class SharedQuestsDynamicRouter : DynamicRouter
{
    private static JsonUtil? _jsonUtil;
    private static HttpResponseUtil? _httpResponseUtil;
    private static SharedQuestsServer? _server;
    private static ISptLogger<SharedQuestsServer>? _logger;

    public SharedQuestsDynamicRouter(JsonUtil jsonUtil, HttpResponseUtil httpResponseUtil)
        : base(jsonUtil, GetCustomRoutes())
    {
        _jsonUtil = jsonUtil;
        _httpResponseUtil = httpResponseUtil;
    }

    public void SetServer(SharedQuestsServer server) => _server = server;

    public void SetLogger(ISptLogger<SharedQuestsServer> logger) => _logger = logger;

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

    /// <summary>
    /// Returns the detail payload for one quest. The matched url is the full request
    /// path (the "/sharedquests/quest/" prefix registered above plus the quest id),
    /// so the quest id is everything after the last '/'.
    /// </summary>
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
        catch (Exception ex)
        {
            _logger?.Error($"[SharedQuests] Error handling quest detail: {ex.Message}");
            return new ValueTask<string>(_httpResponseUtil!.NullResponse());
        }
    }
}

/// <summary>
/// Replaces SPT's ItemEventRouter (DI resolves the highest-TypePriority registration
/// of a base type) to log the Action names carried in /client/game/profile/items/moving
/// request bodies — quest turn-ins/completions arrive as QuestHandover/QuestComplete here.
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton, TypePriority = int.MaxValue)]
public class ActionLoggingItemEventRouter(
    ISptLogger<ActionLoggingItemEventRouter> actionLogger,
    ISptLogger<ItemEventRouter> logger,
    ISptLogger<FileLogger> fileLogger,
    JsonUtil jsonUtil,
    ProfileHelper profileHelper,
    ServerLocalisationService localisationService,
    EventOutputHolder eventOutputHolder,
    IEnumerable<ItemEventRouterDefinition> itemEventRouters,
    ICloner cloner)
    : ItemEventRouter(logger, fileLogger, jsonUtil, profileHelper, localisationService, eventOutputHolder, itemEventRouters, cloner)
{
    public override ValueTask<ItemEventRouterResponse> HandleEvents(ItemEventRouterRequest info, MongoId sessionID)
    {
        var actions = info.Data == null ? "(empty)" : string.Join(", ", info.Data.Select(d => d.Action));
        actionLogger.Info($"[SharedQuests] items/moving actions: {actions}");
        return base.HandleEvents(info, sessionID);
    }
}

/// <summary>
/// Main server class that provides quest status data
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton, TypePriority = int.MaxValue)]
public class SharedQuestsServer(
    ISptLogger<SharedQuestsServer> logger,
    SharedQuestsRouter router,
    SharedQuestsDynamicRouter dynamicRouter,
    QuestHelper questHelper,
    DatabaseService databaseService,
    LocaleService localeService,
    SPTarkov.Server.Core.Servers.SaveServer saveServer,
    JsonUtil jsonUtil) : IOnLoad
{
    // Money item tpls: roubles, dollars, euros
    private static readonly HashSet<string> MoneyTpls =
    [
        "5449016a4bdc2d6f028b456f", "5696686a4bdc2da3298b456a", "569668774bdc2da2298b4568",
    ];

    // Cache quest prerequisites (questId -> list of prerequisite quest names)
    private Dictionary<string, List<string>> _questPrerequisites = new();

    // SPT-free quest metadata for the overview endpoint, built once at load
    private List<QuestMeta> _questMetas = new();

    // location mongo id -> map string id ("bigmap"), from the locations DB
    private Dictionary<string, string> _locationIdToMapId = new();

    public Task OnLoad()
    {
        // Wire up the router
        router.SetServer(this);
        router.SetLogger(logger);
        dynamicRouter.SetServer(this);
        dynamicRouter.SetLogger(logger);

        logger.Info("[SharedQuests] Initializing...");

        // Build quest metadata and location caches
        BuildQuestMetaCache();
        BuildLocationMapCache();

        // Test reading profiles
        var statuses = GetFreshQuestStatuses();
        logger.Success($"[SharedQuests] Found {statuses.Count} profiles with quest data");
        logger.Info("[SharedQuests] Endpoints available: /sharedquests/statuses, /sharedquests/overview, /sharedquests/quest/<id>");

        return Task.CompletedTask;
    }

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

    /// <summary>
    /// Extract all string values from Target which may be a string or ListOrT&lt;string&gt;
    /// Returns a list of quest IDs
    /// </summary>
    private List<string> ExtractTargetStrings(object target)
    {
        var results = new List<string>();
        
        if (target == null) return results;
        
        // If it's already a string, return it as single item
        if (target is string str)
        {
            if (!string.IsNullOrEmpty(str))
                results.Add(str);
            return results;
        }
        
        // If it's IEnumerable<string>, get all elements
        if (target is IEnumerable<string> enumerable)
        {
            results.AddRange(enumerable.Where(s => !string.IsNullOrEmpty(s)));
            return results;
        }
        
        // If it's a generic IEnumerable, try to get all elements
        if (target is System.Collections.IEnumerable nonGenericEnumerable)
        {
            foreach (var item in nonGenericEnumerable)
            {
                if (item is string s && !string.IsNullOrEmpty(s))
                {
                    results.Add(s);
                }
            }
            if (results.Count > 0) return results;
        }
        
        // Check all properties to find the actual value (handles wrapper types like ListOrT<string>)
        foreach (var prop in target.GetType().GetProperties())
        {
            try
            {
                var value = prop.GetValue(target);
                if (value is string valStr && !string.IsNullOrEmpty(valStr))
                {
                    results.Add(valStr);
                }
                else if (value is IEnumerable<string> valEnum)
                {
                    results.AddRange(valEnum.Where(s => !string.IsNullOrEmpty(s)));
                }
            }
            catch { }
        }
        
        return results;
    }

    /// <summary>
    /// Locked reason (prerequisite quest names) — only when the quest is Locked (0).
    /// </summary>
    private string? GetLockedReason(string questId, int statusCode)
    {
        if (statusCode != (int)QuestStatusEnum.Locked) return null;
        if (!_questPrerequisites.TryGetValue(questId, out var prerequisites) || prerequisites.Count == 0)
            return null;
        return string.Join(", ", prerequisites);
    }

    /// <summary>
    /// Quest statuses for all profiles, from live in-memory data.
    /// </summary>
    public Dictionary<string, Dictionary<string, QuestStatusInfo>> GetFreshQuestStatuses()
    {
        var result = new Dictionary<string, Dictionary<string, QuestStatusInfo>>();
        var allQuests = questHelper.GetQuestsFromDb();

        foreach (var parsed in ReadProfilesLive())
        {
            var questStatuses = new Dictionary<string, QuestStatusInfo>();
            foreach (var quest in allQuests)
            {
                int statusCode = parsed.QuestStatusByQid.TryGetValue(quest.Id, out var s) ? s : 0;
                questStatuses[quest.Id] = new QuestStatusInfo
                {
                    Status = statusCode,
                    LockedReason = GetLockedReason(quest.Id, statusCode)
                };
            }
            result[parsed.Nickname] = questStatuses;
        }

        return result;
    }

    /// <summary>
    /// Assemble the overview payload: live profiles + cached quest metadata.
    /// </summary>
    public OverviewResponse GetOverview()
    {
        return OverviewBuilder.Build(_questMetas, ReadProfilesLive(), _locationIdToMapId);
    }

    /// <summary>
    /// Parsed profiles from SPT's live in-memory store (SaveServer), headless excluded.
    /// Disk files lag behind until SPT flushes them, so reading them showed stale quest
    /// state right after a turn-in. Each profile is serialized with SPT's own JsonUtil —
    /// the same shape as the on-disk files — and fed to the existing parser. Never throws.
    /// </summary>
    private List<ParsedProfile> ReadProfilesLive()
    {
        var profiles = new List<ParsedProfile>();
        try
        {
            foreach (var profile in saveServer.GetProfiles().Values)
            {
                try
                {
                    var json = jsonUtil.Serialize(profile);
                    if (json == null) continue;
                    var parsed = ProfileParser.Parse(json);
                    if (parsed == null) continue;
                    if (parsed.Nickname.StartsWith("headless_", StringComparison.OrdinalIgnoreCase)) continue;
                    profiles.Add(parsed);
                }
                catch (Exception ex)
                {
                    logger.Warning($"[SharedQuests] Error reading live profile: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error($"[SharedQuests] Error reading profiles: {ex.Message}");
        }
        return profiles;
    }

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
            var conditionId = condition.Id.ToString();
            if (string.IsNullOrEmpty(conditionId)) continue;
            double? target = condition.Value is > 0 ? condition.Value : null;
            objectives.Add(new ObjectiveMeta { ConditionId = conditionId, Text = L(conditionId, conditionId), Target = target });
        }

        var rewards = new List<RewardMeta>();
        try
        {
            List<Reward> successRewards = quest.Rewards != null && quest.Rewards.TryGetValue("Success", out var s) ? s : [];
            foreach (var reward in successRewards)
            {
                switch (reward.Type)
                {
                    case RewardType.Experience:
                        if (reward.Value is double xp)
                            rewards.Add(new RewardMeta { Kind = "Experience", Value = xp });
                        break;

                    case RewardType.TraderStanding:
                        if (reward.Value is double rep)
                            rewards.Add(new RewardMeta
                            {
                                Kind = "TraderStanding",
                                Name = OverviewBuilder.TraderName(reward.Target),
                                Value = rep,
                            });
                        break;

                    case RewardType.Item:
                        var firstItem = reward.Items?.FirstOrDefault();
                        if (firstItem == null) continue;
                        var tpl = firstItem.Template.ToString();
                        var count = 1;
                        if (reward.Value is double c && c >= 1) count = (int)c;
                        else if (firstItem.Upd?.StackObjectsCount is double stack && stack >= 1) count = (int)stack;
                        var isMoney = MoneyTpls.Contains(tpl);
                        rewards.Add(new RewardMeta
                        {
                            Kind = isMoney ? "Money" : "Item",
                            Name = L($"{tpl} Name", tpl),
                            Count = count,
                        });
                        break;

                    // other kinds (AssortmentUnlock, Skill, ...) intentionally skipped
                }
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

        return QuestDetailBuilder.Build(detailMeta, ReadProfilesLive());
    }
}
