using System.Text;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

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

/// <summary>
/// Main server class that provides quest status data
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton, TypePriority = int.MaxValue)]
public class SharedQuestsServer(
    ISptLogger<SharedQuestsServer> logger,
    SharedQuestsRouter router,
    QuestHelper questHelper,
    DatabaseService databaseService) : IOnLoad
{
    // Path to profiles directory (relative to SPT root)
    private const string ProfilesPath = "user/profiles";

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

        logger.Info("[SharedQuests] Initializing...");

        // Build quest metadata and location caches
        BuildQuestMetaCache();
        BuildLocationMapCache();

        // Test reading profiles
        var statuses = GetFreshQuestStatuses();
        logger.Success($"[SharedQuests] Found {statuses.Count} profiles with quest data");
        logger.Info("[SharedQuests] Endpoints available: /sharedquests/statuses, /sharedquests/overview");

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
    /// Read quest statuses directly from profile JSON files on disk
    /// This ensures we always get the latest saved data, not cached data
    /// </summary>
    public Dictionary<string, Dictionary<string, QuestStatusInfo>> GetFreshQuestStatuses()
    {
        var result = new Dictionary<string, Dictionary<string, QuestStatusInfo>>();
        
        try
        {
            // Get all profile files
            if (!Directory.Exists(ProfilesPath))
            {
                logger.Warning($"[SharedQuests] Profiles directory not found: {ProfilesPath}");
                return result;
            }
            
            var profileFiles = Directory.GetFiles(ProfilesPath, "*.json");
            logger.Debug($"[SharedQuests] Found {profileFiles.Length} profile files");
            
            var allQuests = questHelper.GetQuestsFromDb();
            
            foreach (var profilePath in profileFiles)
            {
                try
                {
                    string json;
                    try { json = File.ReadAllText(profilePath); }
                    catch (Exception ex)
                    {
                        logger.Warning($"[SharedQuests] Error reading profile {profilePath}: {ex.Message}");
                        continue;
                    }

                    var parsed = ProfileParser.Parse(json);
                    if (parsed == null) continue;

                    var nickname = parsed.Nickname;

                    // Skip headless profiles
                    if (nickname.StartsWith("headless_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var questStatuses = new Dictionary<string, QuestStatusInfo>();

                    foreach (var quest in allQuests)
                    {
                        int statusCode = parsed.QuestStatusByQid.TryGetValue(quest.Id, out var s) ? s : 0;
                        var lockedReason = GetLockedReason(quest.Id, statusCode);

                        questStatuses[quest.Id] = new QuestStatusInfo
                        {
                            Status = statusCode,
                            LockedReason = lockedReason
                        };
                    }

                    result[nickname] = questStatuses;

                    // Log a few sample statuses for debugging
                    var samples = questStatuses.Take(3).Select(kv => $"{kv.Key.Substring(0, 8)}...={(QuestStatusEnum)kv.Value.Status}");
                    logger.Debug($"[SharedQuests] Loaded {questStatuses.Count} quest statuses for {nickname} (samples: {string.Join(", ", samples)})");
                }
                catch (Exception ex)
                {
                    logger.Warning($"[SharedQuests] Error reading profile {profilePath}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error($"[SharedQuests] Error reading profiles: {ex.Message}");
        }

        return result;
    }

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
}
