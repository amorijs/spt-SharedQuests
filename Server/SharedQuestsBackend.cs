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
    public override SemanticVersioning.Version Version { get; init; } = new("1.0.0");
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
                "/sharedquests/refresh",
                static async (url, info, sessionId, output) => await HandleRefresh(sessionId)
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
    /// Force refresh endpoint (same as regular get now)
    /// </summary>
    private static ValueTask<string> HandleRefresh(MongoId sessionId)
    {
        try
        {
            _logger?.Info("[SharedQuests] Refresh requested");
            var freshData = _server?.GetFreshQuestStatuses();
            return new ValueTask<string>(_jsonUtil!.Serialize(new { success = true, profiles = freshData?.Count ?? 0 })!);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SharedQuests] Error refreshing: {ex.Message}");
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
    QuestHelper questHelper) : IOnLoad
{
    // Path to profiles directory (relative to SPT root)
    private const string ProfilesPath = "user/profiles";
    
    // Cache quest prerequisites (questId -> list of prerequisite quest names)
    private Dictionary<string, List<string>> _questPrerequisites = new();
    
    public Task OnLoad()
    {
        // Wire up the router
        router.SetServer(this);
        router.SetLogger(logger);
        
        logger.Info("[SharedQuests] Initializing...");
        
        // Build prerequisite cache
        BuildPrerequisiteCache();
        
        // Test reading profiles
        var statuses = GetFreshQuestStatuses();
        logger.Success($"[SharedQuests] Found {statuses.Count} profiles with quest data");
        logger.Info("[SharedQuests] Real-time endpoint available at /sharedquests/statuses");
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Build a cache of quest prerequisites for quick lookup
    /// </summary>
    private void BuildPrerequisiteCache()
    {
        try
        {
            var allQuests = questHelper.GetQuestsFromDb();
            var questNameById = allQuests.ToDictionary(q => q.Id.ToString(), q => q.QuestName ?? q.Name ?? "Unknown");
            
            foreach (var quest in allQuests)
            {
                var prerequisites = new List<string>();
                
                // Check AvailableForStart conditions for Quest type
                if (quest.Conditions?.AvailableForStart != null)
                {
                    foreach (var condition in quest.Conditions.AvailableForStart)
                    {
                        // Check if this is a Quest condition
                        if (condition.ConditionType == "Quest" && condition.Target != null)
                        {
                            // Target can be a string or ListOrT<string> - extract all quest IDs
                            var targetQuestIds = ExtractTargetStrings(condition.Target);
                            
                            foreach (var targetQuestId in targetQuestIds)
                            {
                                if (questNameById.TryGetValue(targetQuestId, out var prereqName))
                                {
                                    prerequisites.Add(prereqName);
                                }
                                else
                                {
                                    // Fallback - use the ID
                                    prerequisites.Add(targetQuestId);
                                }
                            }
                        }
                    }
                }
                
                if (prerequisites.Count > 0)
                {
                    _questPrerequisites[quest.Id] = prerequisites;
                    logger.Debug($"[SharedQuests] Quest '{quest.QuestName}' requires: {string.Join(", ", prerequisites)}");
                }
            }
            
            logger.Info($"[SharedQuests] Built prerequisite cache for {_questPrerequisites.Count} quests");
        }
        catch (Exception ex)
        {
            logger.Error($"[SharedQuests] Error building prerequisite cache: {ex.Message}");
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
    /// Get the locked reason for a quest (prerequisite quest names)
    /// </summary>
    private string? GetLockedReason(string questId, ProfileData profile)
    {
        // Only provide locked reason if quest is actually locked
        var status = GetQuestStatusFromProfile(profile, questId);
        if (status != QuestStatusEnum.Locked)
        {
            return null;
        }
        
        // Check if we have prerequisite info for this quest
        if (!_questPrerequisites.TryGetValue(questId, out var prerequisites) || prerequisites.Count == 0)
        {
            return null;
        }
        
        // Join all prerequisites with commas
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
                    var profileData = ReadProfileFromDisk(profilePath);
                    if (profileData == null) continue;
                    
                    var nickname = profileData.Nickname;
                    if (string.IsNullOrEmpty(nickname)) continue;
                    
                    // Skip headless profiles
                    if (nickname.StartsWith("headless_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    
                    var questStatuses = new Dictionary<string, QuestStatusInfo>();
                    
                    foreach (var quest in allQuests)
                    {
                        var status = GetQuestStatusFromProfile(profileData, quest.Id);
                        var lockedReason = GetLockedReason(quest.Id, profileData);
                        
                        questStatuses[quest.Id] = new QuestStatusInfo
                        {
                            Status = (int)status,
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
    /// Simple profile data structure for reading from disk
    /// </summary>
    private class ProfileData
    {
        public string? Nickname { get; set; }
        public List<QuestData>? Quests { get; set; }
    }
    
    private class QuestData
    {
        public string? Qid { get; set; }
        public int Status { get; set; }
    }

    /// <summary>
    /// Read and parse a profile JSON file from disk
    /// </summary>
    private ProfileData? ReadProfileFromDisk(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            // Navigate to characters.pmc.Info.Nickname
            if (!root.TryGetProperty("characters", out var characters)) return null;
            if (!characters.TryGetProperty("pmc", out var pmc)) return null;
            if (!pmc.TryGetProperty("Info", out var info)) return null;
            if (!info.TryGetProperty("Nickname", out var nicknameElement)) return null;
            
            var nickname = nicknameElement.GetString();
            if (string.IsNullOrEmpty(nickname)) return null;
            
            // Get quests array from characters.pmc.Quests
            var quests = new List<QuestData>();
            if (pmc.TryGetProperty("Quests", out var questsElement) && questsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var questElement in questsElement.EnumerateArray())
                {
                    if (questElement.TryGetProperty("qid", out var qidElement) &&
                        questElement.TryGetProperty("status", out var statusElement))
                    {
                        quests.Add(new QuestData
                        {
                            Qid = qidElement.GetString(),
                            Status = statusElement.ValueKind == JsonValueKind.Number 
                                ? statusElement.GetInt32() 
                                : (int)Enum.Parse<QuestStatusEnum>(statusElement.GetString() ?? "Locked")
                        });
                    }
                }
            }
            
            return new ProfileData
            {
                Nickname = nickname,
                Quests = quests
            };
        }
        catch (Exception ex)
        {
            logger.Debug($"[SharedQuests] Failed to parse profile {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get quest status from parsed profile data
    /// </summary>
    private QuestStatusEnum GetQuestStatusFromProfile(ProfileData profile, string questId)
    {
        if (profile.Quests == null) return QuestStatusEnum.Locked;
        
        var quest = profile.Quests.FirstOrDefault(q => q.Qid == questId);
        if (quest == null) return QuestStatusEnum.Locked;
        
        return (QuestStatusEnum)quest.Status;
    }

    /// <summary>
    /// Get display name and color for quest status
    /// </summary>
    public static (string Name, string Color) GetStatusInfo(QuestStatusEnum status)
    {
        return status switch
        {
            QuestStatusEnum.Locked => ("Locked", "#808080"),
            QuestStatusEnum.AvailableForStart => ("Available", "#FFD700"),
            QuestStatusEnum.Started => ("Started", "#FFA500"),
            QuestStatusEnum.AvailableForFinish => ("Ready!", "#00FF00"),
            QuestStatusEnum.Success => ("Completed", "#32CD32"),
            QuestStatusEnum.Fail => ("Failed", "#FF4444"),
            QuestStatusEnum.FailRestartable => ("Failed (Retry)", "#FF6600"),
            QuestStatusEnum.MarkedAsFailed => ("Failed", "#FF4444"),
            QuestStatusEnum.Expired => ("Expired", "#666666"),
            QuestStatusEnum.AvailableAfter => ("Timed", "#87CEEB"),
            _ => ($"Unknown", "#FFFFFF")
        };
    }
}
