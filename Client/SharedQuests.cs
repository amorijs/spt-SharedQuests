using BepInEx;
using BepInEx.Logging;
using EFT.Quests;
using EFT.UI;
using HarmonyLib;
using Newtonsoft.Json;
using SPT.Common.Http;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SharedQuests
{
    /// <summary>
    /// Quest status info from server
    /// </summary>
    public class QuestStatusInfo
    {
        public int Status { get; set; }
        public string LockedReason { get; set; }
    }

    [BepInPlugin("com.sharedquests.client", "SharedQuests", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;
        public static Plugin Instance;
        
        // Cache: ProfileName -> { QuestId -> QuestStatusInfo }
        public static Dictionary<string, Dictionary<string, QuestStatusInfo>> QuestStatuses = new Dictionary<string, Dictionary<string, QuestStatusInfo>>();
        public static DateTime LastFetch = DateTime.MinValue;
        public const int CacheDurationSeconds = 5;
        
        // Marker to identify our status section
        public const string STATUS_MARKER_START = "--- Shared Quest Status ---";
        public const string STATUS_MARKER_END = "--------------------------";
        
        // Track current quest being viewed
        public static string CurrentQuestId = null;
        
        // Store reference to TasksScreen for updates
        public static TasksScreen CurrentTasksScreen = null;
        
        // Monitor coroutine reference
        private static Coroutine _monitorCoroutine = null;

        private void Awake()
        {
            Instance = this;
            LogSource = Logger;
            LogSource.LogInfo("SharedQuests client loading...");

            // Initialize settings
            Settings.Init(Config);

            var harmony = new Harmony("com.sharedquests.client");
            harmony.PatchAll();

            LogSource.LogInfo("SharedQuests client loaded!");
        }

        private void Start()
        {
            // Fetch profiles after a short delay to ensure server connection is ready
            StartCoroutine(InitialFetch());
        }

        private IEnumerator InitialFetch()
        {
            // Wait a bit for the game to fully initialize
            yield return new WaitForSeconds(2f);
            
            LogSource.LogInfo("SharedQuests: Performing initial profile fetch...");
            FetchQuestStatuses(force: true);
        }

        /// <summary>
        /// Fetch quest statuses from the server
        /// </summary>
        public static bool FetchQuestStatuses(bool force = false)
        {
            // Check cache freshness
            if (!force && (DateTime.UtcNow - LastFetch).TotalSeconds < CacheDurationSeconds)
            {
                return false; // Using cached data
            }

            try
            {
                LogSource.LogDebug("SharedQuests: Fetching quest statuses from server...");
                var response = RequestHandler.GetJson("/sharedquests/statuses");
                
                if (!string.IsNullOrEmpty(response))
                {
                    var data = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, QuestStatusInfo>>>(response);
                    if (data != null)
                    {
                        QuestStatuses = data;
                        LastFetch = DateTime.UtcNow;
                        LogSource.LogInfo($"SharedQuests: Fetched statuses for {QuestStatuses.Count} profiles");
                        
                        // Update settings with profile list for F12 menu
                        Settings.UpdateProfileList(QuestStatuses.Keys);
                        
                        return true; // Fresh data
                    }
                }
            }
            catch (Exception ex)
            {
                LogSource.LogError($"SharedQuests: Error fetching statuses: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Get status display name
        /// </summary>
        public static string GetStatusName(int status)
        {
            return status switch
            {
                0 => "Locked",
                1 => "Available",
                2 => "Started",
                3 => "Ready!",
                4 => "Completed",
                5 => "Failed",
                6 => "Failed (Retry)",
                7 => "Failed",
                8 => "Expired",
                9 => "Timed",
                _ => "Unknown"
            };
        }

        /// <summary>
        /// Get status color
        /// </summary>
        public static string GetStatusColor(int status)
        {
            return status switch
            {
                0 => "#808080",
                1 => "#FFD700",
                2 => "#FFA500",
                3 => "#00FF00",
                4 => "#32CD32",
                5 => "#FF4444",
                6 => "#FF6600",
                7 => "#FF4444",
                8 => "#666666",
                9 => "#87CEEB",
                _ => "#FFFFFF"
            };
        }

        /// <summary>
        /// Build status text (generic, since we don't know quest ID when monitoring)
        /// </summary>
        public static string BuildStatusTextGeneric()
        {
            if (QuestStatuses.Count == 0)
            {
                return $"<color=#9A8866>{STATUS_MARKER_START}</color>\n<color=#888888>Loading...</color>\n<color=#9A8866>{STATUS_MARKER_END}</color>";
            }

            // When we don't have quest ID, show placeholder
            // The status will be for whatever quest is currently displayed
            return null;
        }

        /// <summary>
        /// Build status text for a quest (rich text version)
        /// </summary>
        public static string BuildStatusTextRich(string questId)
        {
            // Check if mod is enabled
            if (!Settings.Enabled.Value)
            {
                return "";
            }
            
            if (QuestStatuses.Count == 0 || string.IsNullOrEmpty(questId))
            {
                return $"<color=#9A8866>{STATUS_MARKER_START}</color>\n<color=#888888>Loading...</color>\n<color=#9A8866>{STATUS_MARKER_END}</color>";
            }

            var lines = new List<string>();
            lines.Add($"<color=#9A8866>{STATUS_MARKER_START}</color>");

            int visibleCount = 0;
            foreach (var kvp in QuestStatuses)
            {
                var profileName = kvp.Key;
                
                // Skip profiles that are not visible (excluded in settings)
                if (!Settings.IsProfileVisible(profileName))
                {
                    continue;
                }
                
                var quests = kvp.Value;
                int status = 0;
                string lockedReason = null;
                
                if (quests.TryGetValue(questId, out var statusInfo))
                {
                    status = statusInfo.Status;
                    lockedReason = statusInfo.LockedReason;
                }
                
                var statusName = GetStatusName(status);
                var statusColor = GetStatusColor(status);
                
                // Add locked reason if available (status 0 = Locked)
                string statusDisplay;
                if (status == 0 && !string.IsNullOrEmpty(lockedReason))
                {
                    statusDisplay = $"<color={statusColor}>{statusName}</color> <color=#666666>({lockedReason})</color>";
                }
                else
                {
                    statusDisplay = $"<color={statusColor}>{statusName}</color>";
                }
                
                lines.Add($"<color=#CCCCCC>{profileName}:</color> {statusDisplay}");
                visibleCount++;
            }

            // If no profiles are visible, show a message
            if (visibleCount == 0)
            {
                lines.Add("<color=#888888>No profiles selected</color>");
            }

            lines.Add($"<color=#9A8866>{STATUS_MARKER_END}</color>");
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Remove existing status section from text
        /// </summary>
        public static string RemoveStatusSection(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            
            // Match both plain and rich text versions
            var pattern = @"(<color=#\w+>)?---\s*Shared Quest Status\s*---(<\/color>)?[\s\S]*?(<color=#\w+>)?-{20,}(<\/color>)?\s*";
            return Regex.Replace(text, pattern, "").TrimStart();
        }

        /// <summary>
        /// Inject status text into a description, replacing any existing status section
        /// </summary>
        public static string InjectStatusText(string originalText, string questId)
        {
            if (string.IsNullOrEmpty(questId)) return originalText;
            
            // Remove any existing status section first
            var cleanText = RemoveStatusSection(originalText);
            
            // Build fresh status text
            var statusText = BuildStatusTextRich(questId);
            
            // If status text is empty (mod disabled), just return clean text
            if (string.IsNullOrEmpty(statusText))
            {
                return cleanText;
            }
            
            // Prepend status text
            return statusText + "\n\n" + cleanText;
        }

        /// <summary>
        /// Start the monitor coroutine
        /// </summary>
        public static void StartMonitor()
        {
            if (Instance == null) return;
            
            StopMonitor();
            _monitorCoroutine = Instance.StartCoroutine(MonitorDescriptionLabel());
            LogSource.LogInfo("SharedQuests: Started description monitor");
        }

        /// <summary>
        /// Stop the monitor coroutine
        /// </summary>
        public static void StopMonitor()
        {
            if (_monitorCoroutine != null && Instance != null)
            {
                Instance.StopCoroutine(_monitorCoroutine);
                _monitorCoroutine = null;
            }
        }

        /// <summary>
        /// Try to find the currently selected quest ID from the TasksPanel
        /// </summary>
        public static string GetSelectedQuestId()
        {
            if (CurrentTasksScreen == null) return null;
            
            try
            {
                // Find TasksPanel
                var tasksPanels = CurrentTasksScreen.GetComponentsInChildren<TasksPanel>(true);
                foreach (var panel in tasksPanels)
                {
                    // Try to get the selected quest using reflection
                    var fields = panel.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (var field in fields)
                    {
                        // Look for QuestClass field
                        if (field.FieldType == typeof(QuestClass) || field.FieldType.Name.Contains("Quest"))
                        {
                            var value = field.GetValue(panel);
                            if (value is QuestClass quest && quest.Template?.Id != null)
                            {
                                return quest.Template.Id;
                            }
                        }
                    }
                    
                    // Also try properties
                    var props = panel.GetType().GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    foreach (var prop in props)
                    {
                        if (prop.PropertyType == typeof(QuestClass) || prop.PropertyType.Name.Contains("Quest"))
                        {
                            try
                            {
                                var value = prop.GetValue(panel);
                                if (value is QuestClass quest && quest.Template?.Id != null)
                                {
                                    return quest.Template.Id;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogSource.LogError($"SharedQuests: Error getting selected quest: {ex.Message}");
            }
            
            return CurrentQuestId; // Fallback
        }

        /// <summary>
        /// Continuously monitor the DescriptionLabel and inject status when it changes
        /// </summary>
        public static IEnumerator MonitorDescriptionLabel()
        {
            string lastCleanText = "";
            string lastQuestId = "";
            TextMeshProUGUI descriptionLabel = null;
            
            while (CurrentTasksScreen != null)
            {
                // Find the DescriptionLabel if we don't have it or it's destroyed
                if (descriptionLabel == null || !descriptionLabel)
                {
                    var tmpComponents = CurrentTasksScreen.GetComponentsInChildren<TextMeshProUGUI>(true);
                    foreach (var tmp in tmpComponents)
                    {
                        if (tmp.name == "DescriptionLabel" && tmp.text != null && tmp.text.Length > 50)
                        {
                            descriptionLabel = tmp;
                            descriptionLabel.richText = true;
                            break;
                        }
                    }
                }
                
                if (descriptionLabel != null && descriptionLabel.text != null)
                {
                    // Get the clean text (without our status section)
                    string currentCleanText = RemoveStatusSection(descriptionLabel.text);
                    
                    // Check if the description changed (new quest selected)
                    bool descriptionChanged = currentCleanText != lastCleanText;
                    bool needsMarker = !descriptionLabel.text.Contains("Shared Quest Status");
                    
                    if (descriptionChanged || needsMarker)
                    {
                        // Try to find the quest ID from the UI hierarchy
                        string questId = FindQuestFromDescriptionPanel(descriptionLabel);
                        
                        // Fallback to other methods if needed
                        if (string.IsNullOrEmpty(questId))
                        {
                            questId = GetSelectedQuestId();
                        }
                        
                        if (!string.IsNullOrEmpty(questId) && questId != lastQuestId)
                        {
                            CurrentQuestId = questId;
                            LogSource.LogDebug($"SharedQuests: Detected quest change to {questId}");
                        }
                        
                        if (!string.IsNullOrEmpty(questId))
                        {
                            var newText = InjectStatusText(descriptionLabel.text, questId);
                            descriptionLabel.text = newText;
                            descriptionLabel.ForceMeshUpdate();
                            
                            lastCleanText = RemoveStatusSection(newText);
                            lastQuestId = questId;
                            LogSource.LogDebug($"SharedQuests: Monitor injected status for quest {questId}");
                        }
                    }
                }
                
                yield return new WaitForSeconds(0.1f);
            }
            
            LogSource.LogInfo("SharedQuests: Monitor stopped (TasksScreen closed)");
        }

        /// <summary>
        /// Try to find the quest from the QuestDescription component in the hierarchy
        /// </summary>
        public static string FindQuestFromDescriptionPanel(TextMeshProUGUI descriptionLabel)
        {
            try
            {
                // Walk up the hierarchy to find QuestDescription component
                Transform current = descriptionLabel.transform;
                while (current != null)
                {
                    // Look for any component that might have quest info
                    var components = current.GetComponents<MonoBehaviour>();
                    foreach (var comp in components)
                    {
                        if (comp == null) continue;
                        
                        var compType = comp.GetType();
                        
                        // Check all fields for QuestClass
                        var fields = compType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        foreach (var field in fields)
                        {
                            if (field.FieldType == typeof(QuestClass))
                            {
                                var quest = field.GetValue(comp) as QuestClass;
                                if (quest?.Template?.Id != null)
                                {
                                    LogSource.LogDebug($"SharedQuests: Found quest {quest.Template.Id} in {compType.Name}.{field.Name}");
                                    return quest.Template.Id;
                                }
                            }
                        }
                    }
                    
                    current = current.parent;
                }
            }
            catch (Exception ex)
            {
                LogSource.LogError($"SharedQuests: Error finding quest from panel: {ex.Message}");
            }
            
            return null;
        }
    }

    /// <summary>
    /// Patch TasksScreen to start monitoring
    /// </summary>
    [HarmonyPatch(typeof(TasksScreen), "Show")]
    class TasksScreenShowPatch
    {
        [HarmonyPostfix]
        static void Postfix(TasksScreen __instance)
        {
            try
            {
                Plugin.LogSource.LogInfo("SharedQuests: TasksScreen.Show");
                
                // Store reference
                Plugin.CurrentTasksScreen = __instance;
                
                // Fetch fresh data from server
                Plugin.FetchQuestStatuses(force: true);

                // Enable rich text on all TMP components
                var tmpComponents = __instance.GetComponentsInChildren<TextMeshProUGUI>(true);
                foreach (var tmp in tmpComponents)
                {
                    tmp.richText = true;
                }
                
                // Start continuous monitoring
                Plugin.StartMonitor();
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"SharedQuests: Error in TasksScreen patch: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patch TasksScreen.Close to stop monitoring
    /// </summary>
    [HarmonyPatch(typeof(TasksScreen), "Close")]
    class TasksScreenClosePatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            try
            {
                Plugin.LogSource.LogInfo("SharedQuests: TasksScreen.Close");
                Plugin.CurrentTasksScreen = null;
                Plugin.StopMonitor();
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"SharedQuests: Error in TasksScreen.Close patch: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patch NotesTask.Show to track current quest ID
    /// </summary>
    [HarmonyPatch(typeof(NotesTask), "Show")]
    class NotesTaskShowPatch
    {
        [HarmonyPostfix]
        static void Postfix(NotesTask __instance, QuestClass quest)
        {
            try
            {
                if (quest?.Template?.Id == null) return;
                
                Plugin.CurrentQuestId = quest.Template.Id;
                Plugin.LogSource.LogDebug($"SharedQuests: NotesTask.Show - quest {Plugin.CurrentQuestId}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"SharedQuests: Error in NotesTask patch: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patch TasksPanel.Show to enable rich text
    /// </summary>
    [HarmonyPatch]
    class TasksPanelShowPatch
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(TasksPanel), "Show");
        }

        [HarmonyPostfix]
        static void Postfix(TasksPanel __instance)
        {
            try
            {
                var tmpComponents = __instance.GetComponentsInChildren<TextMeshProUGUI>(true);
                foreach (var tmp in tmpComponents)
                {
                    tmp.richText = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"SharedQuests: Error in TasksPanel patch: {ex.Message}");
            }
        }
    }
}

