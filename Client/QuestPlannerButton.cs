using EFT.UI;
using HarmonyLib;
using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SharedQuests
{
    /// <summary>
    /// Injects a "QUESTS" button into the main menu taskbar that toggles the planner panel.
    /// </summary>
    internal static class QuestPlannerButton
    {
        private static GameObject _button;

        public static void TryInject()
        {
            if (_button) return;

            var taskBar = UnityEngine.Object.FindObjectOfType<MenuTaskBar>(true);
            if (taskBar == null)
            {
                Plugin.LogSource.LogWarning("SharedQuests: MenuTaskBar not found in scene");
                return;
            }

            // The nav row is the HorizontalLayoutGroup with the most children
            HorizontalLayoutGroup best = null;
            int bestCount = 0;
            foreach (var hlg in taskBar.GetComponentsInChildren<HorizontalLayoutGroup>(true))
            {
                if (hlg.transform.childCount > bestCount)
                {
                    bestCount = hlg.transform.childCount;
                    best = hlg;
                }
            }
            if (best == null)
            {
                Plugin.LogSource.LogWarning("SharedQuests: Could not find nav HorizontalLayoutGroup in MenuTaskBar");
                return;
            }

            _button = BuildNavButton(best.transform);
        }

        private static GameObject BuildNavButton(Transform parent)
        {
            var siblingLabel = parent.GetComponentInChildren<TextMeshProUGUI>(true);
            float fontSize = siblingLabel != null ? siblingLabel.fontSize : 12f;
            Color normalColor = siblingLabel != null ? siblingLabel.color : new Color(0.85f, 0.85f, 0.85f);
            float charSpacing = siblingLabel != null ? siblingLabel.characterSpacing : 2f;

            var go = new GameObject("SharedQuestsPlannerButton");
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();

            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 110f;
            le.flexibleWidth = 0f;

            var bg = go.AddComponent<Image>();
            bg.color = Color.clear;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(8f, 0f);
            labelRt.offsetMax = new Vector2(-4f, 0f);
            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = "QUESTS";
            label.fontSize = fontSize;
            label.fontStyle = FontStyles.Bold;
            label.color = normalColor;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.characterSpacing = charSpacing;
            label.overflowMode = TextOverflowModes.Ellipsis;
            if (siblingLabel != null && siblingLabel.font != null) label.font = siblingLabel.font;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => QuestPlannerPanel.Instance.Toggle());

            Color hover = QuestPlannerPanel.Accent;
            Color dim = new Color(normalColor.r * 0.6f, normalColor.g * 0.6f, normalColor.b * 0.6f);
            var et = go.AddComponent<EventTrigger>();
            AddTrigger(et, EventTriggerType.PointerEnter, _ => label.color = hover);
            AddTrigger(et, EventTriggerType.PointerExit, _ => label.color = normalColor);
            AddTrigger(et, EventTriggerType.PointerDown, _ => label.color = dim);
            AddTrigger(et, EventTriggerType.PointerUp, _ => label.color = hover);

            go.SetActive(true);
            return go;
        }

        private static void AddTrigger(EventTrigger et, EventTriggerType type,
            UnityEngine.Events.UnityAction<BaseEventData> action)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(action);
            et.triggers.Add(entry);
        }
    }

    /// <summary>
    /// Inject the taskbar button one frame after the main menu awakes.
    /// </summary>
    [HarmonyPatch(typeof(MenuScreen), "Awake")]
    internal class MenuScreenAwakePatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            QuestPlannerPanel.Instance.StartCoroutine(InjectNextFrame());
        }

        private static IEnumerator InjectNextFrame()
        {
            yield return null;
            try
            {
                QuestPlannerButton.TryInject();
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"SharedQuests: Planner button inject failed: {ex.Message}");
            }
        }
    }
}
