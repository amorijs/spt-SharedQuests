using System.Text;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

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
/// Main server class that modifies quest descriptions to show all profiles' status
/// </summary>
[Injectable(TypePriority = int.MaxValue)] // Load after everything so all quests exist
public class SharedQuestsServer(
    ISptLogger<SharedQuestsServer> logger,
    DatabaseService databaseService,
    ProfileHelper profileHelper,
    QuestHelper questHelper) : IOnLoad
{
    public Task OnLoad()
    {
        logger.Info("[SharedQuests] Loading quest status for all profiles...");
        
        UpdateAllQuestDescriptions();
        
        logger.Success("[SharedQuests] Quest descriptions updated with shared status info!");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Update all quest descriptions to include status from all profiles
    /// </summary>
    private void UpdateAllQuestDescriptions()
    {
        var allQuests = questHelper.GetQuestsFromDb();
        var profileStatuses = GetAllProfileQuestStatuses();

        if (profileStatuses.Count == 0)
        {
            logger.Warning("[SharedQuests] No profiles found, skipping quest description updates.");
            return;
        }

        logger.Info($"[SharedQuests] Found {profileStatuses.Count} profiles, updating {allQuests.Count} quest descriptions...");

        foreach (var quest in allQuests)
        {
            var questId = quest.Id;
            var descriptionKey = $"{questId} description";

            // Build the status text for this quest
            var statusText = BuildStatusText(questId, profileStatuses);

            // Update all language locales
            foreach (var (langCode, globalLocales) in databaseService.GetLocales().Global)
            {
                globalLocales.AddTransformer(localeDict =>
                {
                    if (localeDict!.TryGetValue(descriptionKey, out var originalDescription))
                    {
                        localeDict[descriptionKey] = statusText + "\n\n" + originalDescription;
                    }
                    return localeDict;
                });
            }
        }
    }

    /// <summary>
    /// Get quest statuses for all profiles
    /// Returns: { ProfileName -> { QuestId -> Status } }
    /// </summary>
    private Dictionary<string, Dictionary<string, QuestStatusEnum>> GetAllProfileQuestStatuses()
    {
        var result = new Dictionary<string, Dictionary<string, QuestStatusEnum>>();
        var allQuests = questHelper.GetQuestsFromDb();
        var profiles = profileHelper.GetProfiles();

        foreach (var profileKvp in profiles)
        {
            var sessionId = profileKvp.Key;
            var pmcProfile = profileHelper.GetPmcProfile(sessionId);

            if (pmcProfile?.Info?.Nickname == null)
            {
                continue;
            }

            var profileName = pmcProfile.Info.Nickname;
            
            // Skip headless profiles (server-side bots/automation)
            if (profileName.StartsWith("headless_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            
            var questStatuses = new Dictionary<string, QuestStatusEnum>();

            foreach (var quest in allQuests)
            {
                var questStatus = pmcProfile.GetQuestStatus(quest.Id);
                questStatuses[quest.Id] = questStatus;
            }

            result[profileName] = questStatuses;
        }

        return result;
    }

    /// <summary>
    /// Build the status text showing all profiles' status for a quest
    /// </summary>
    private string BuildStatusText(string questId, Dictionary<string, Dictionary<string, QuestStatusEnum>> profileStatuses)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<color=#9A8866>--- Shared Quest Status ---</color>");

        foreach (var (profileName, questStatuses) in profileStatuses)
        {
            var status = questStatuses.TryGetValue(questId, out var s) ? s : QuestStatusEnum.Locked;
            var (statusName, statusColor) = GetStatusInfo(status);
            
            sb.AppendLine($"<color=#CCCCCC>{profileName}:</color> <color={statusColor}>{statusName}</color>");
        }

        sb.Append("<color=#9A8866>--------------------------</color>");
        return sb.ToString();
    }

    /// <summary>
    /// Get display name and color for quest status
    /// </summary>
    private static (string Name, string Color) GetStatusInfo(QuestStatusEnum status)
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
