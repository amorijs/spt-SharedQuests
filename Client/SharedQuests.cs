using BepInEx;
using BepInEx.Logging;
using EFT.UI;
using HarmonyLib;
using System;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace SharedQuests
{
    [BepInPlugin("com.sharedquests.client", "SharedQuests", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;

        private void Awake()
        {
            LogSource = Logger;
            LogSource.LogInfo("SharedQuests client loading...");

            var harmony = new Harmony("com.sharedquests.client");
            harmony.PatchAll();

            LogSource.LogInfo("SharedQuests client loaded - rich text patches applied!");
        }
    }

    /// <summary>
    /// Patch NotesTask to enable rich text on the description field
    /// This allows color tags to render in the Character -> Tasks menu
    /// </summary>
    [HarmonyPatch]
    class NotesTaskDescriptionPatch
    {
        static MethodBase TargetMethod()
        {
            // Target NotesTask.Show method
            var notesTaskType = typeof(NotesTask);
            var method = AccessTools.Method(notesTaskType, "Show");
            
            if (method != null)
            {
                Plugin.LogSource.LogInfo($"SharedQuests: Found NotesTask.Show method");
            }
            else
            {
                Plugin.LogSource.LogWarning($"SharedQuests: Could not find NotesTask.Show method");
            }
            
            return method;
        }

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            try
            {
                EnableRichTextOnDescription(__instance);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"SharedQuests: Error in NotesTask patch: {ex.Message}");
            }
        }

        private static void EnableRichTextOnDescription(object instance)
        {
            if (instance == null) return;

            var instanceType = instance.GetType();
            
            // Try to find description-related TextMeshProUGUI fields
            var fields = instanceType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            
            foreach (var field in fields)
            {
                // Look for TextMeshProUGUI fields that might be the description
                if (field.FieldType == typeof(TextMeshProUGUI))
                {
                    var tmp = field.GetValue(instance) as TextMeshProUGUI;
                    if (tmp != null && !tmp.richText)
                    {
                        tmp.richText = true;
                        Plugin.LogSource.LogDebug($"SharedQuests: Enabled richText on {field.Name}");
                    }
                }
            }

            // Also check the GameObject hierarchy for any TMP components
            var monoBehaviour = instance as MonoBehaviour;
            if (monoBehaviour != null)
            {
                var tmpComponents = monoBehaviour.GetComponentsInChildren<TextMeshProUGUI>(true);
                foreach (var tmp in tmpComponents)
                {
                    if (!tmp.richText)
                    {
                        tmp.richText = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Patch TasksPanel to enable rich text on task descriptions
    /// </summary>
    [HarmonyPatch]
    class TasksPanelPatch
    {
        static MethodBase TargetMethod()
        {
            var tasksPanelType = typeof(TasksPanel);
            // Try Show method first
            var method = AccessTools.Method(tasksPanelType, "Show");
            
            if (method != null)
            {
                Plugin.LogSource.LogInfo($"SharedQuests: Found TasksPanel.Show method");
            }
            
            return method;
        }

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            try
            {
                // Enable rich text on all TMP components in the panel
                var monoBehaviour = __instance as MonoBehaviour;
                if (monoBehaviour != null)
                {
                    var tmpComponents = monoBehaviour.GetComponentsInChildren<TextMeshProUGUI>(true);
                    int count = 0;
                    foreach (var tmp in tmpComponents)
                    {
                        if (!tmp.richText)
                        {
                            tmp.richText = true;
                            count++;
                        }
                    }
                    
                    if (count > 0)
                    {
                        Plugin.LogSource.LogDebug($"SharedQuests: Enabled richText on {count} TMP components in TasksPanel");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"SharedQuests: Error in TasksPanel patch: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patch TasksScreen for good measure
    /// </summary>
    [HarmonyPatch(typeof(TasksScreen), "Show")]
    class TasksScreenPatch
    {
        [HarmonyPostfix]
        static void Postfix(TasksScreen __instance)
        {
            try
            {
                var tmpComponents = __instance.GetComponentsInChildren<TextMeshProUGUI>(true);
                foreach (var tmp in tmpComponents)
                {
                    if (!tmp.richText)
                    {
                        tmp.richText = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"SharedQuests: Error in TasksScreen patch: {ex.Message}");
            }
        }
    }
}
