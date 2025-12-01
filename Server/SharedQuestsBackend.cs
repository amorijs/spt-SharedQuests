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
                        $"{kv.Key}={(QuestStatusEnum)(kv.Value.TryGetValue(firstQuestId, out var s) ? s : 0)}");
                    _logger?.Debug($"[SharedQuests] Quest {firstQuestId.Substring(0, 12)}... status: {string.Join(", ", statuses)}");
                }
            }
            
            return new ValueTask<string>(_jsonUtil!.Serialize(freshData ?? new Dictionary<string, Dictionary<string, int>>())!);
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
    
    public Task OnLoad()
    {
        // Wire up the router
        router.SetServer(this);
        router.SetLogger(logger);
        
        logger.Info("[SharedQuests] Initializing...");
        
        // Test reading profiles
        var statuses = GetFreshQuestStatuses();
        logger.Success($"[SharedQuests] Found {statuses.Count} profiles with quest data");
        logger.Info("[SharedQuests] Real-time endpoint available at /sharedquests/statuses");
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Read quest statuses directly from profile JSON files on disk
    /// This ensures we always get the latest saved data, not cached data
    /// </summary>
    public Dictionary<string, Dictionary<string, int>> GetFreshQuestStatuses()
    {
        var result = new Dictionary<string, Dictionary<string, int>>();
        
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
                    
                    var questStatuses = new Dictionary<string, int>();
                    
                    foreach (var quest in allQuests)
                    {
                        var status = GetQuestStatusFromProfile(profileData, quest.Id);
                        questStatuses[quest.Id] = (int)status;
                    }
                    
                    result[nickname] = questStatuses;
                    
                    // Log a few sample statuses for debugging
                    var samples = questStatuses.Take(3).Select(kv => $"{kv.Key.Substring(0, 8)}...={(QuestStatusEnum)kv.Value}");
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
