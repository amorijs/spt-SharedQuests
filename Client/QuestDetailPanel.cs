using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using SPT.Common.Http;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SharedQuests
{
    /// <summary>Detail payload from /sharedquests/quest/&lt;id&gt; (mirrors server DTOs).</summary>
    public class QuestDetailObjectiveProgress
    {
        public int? Count { get; set; }
        public bool Done { get; set; }
    }

    public class QuestDetailObjective
    {
        public string Text { get; set; }
        public double? Target { get; set; }
        public Dictionary<string, QuestDetailObjectiveProgress> Progress { get; set; }
    }

    public class QuestDetailPrereq
    {
        public string Name { get; set; }
        public Dictionary<string, int> Statuses { get; set; }
    }

    public class QuestDetailResponse
    {
        public string Name { get; set; }
        public string Trader { get; set; }
        public List<string> Maps { get; set; }
        public string Description { get; set; }
        public List<QuestDetailObjective> Objectives { get; set; }
        public List<QuestDetailPrereq> Prereqs { get; set; }
        public List<string> Rewards { get; set; }
    }

    /// <summary>
    /// Stacked overlay showing one quest's details on top of the planner.
    /// Plain class (not a MonoBehaviour): ESC handling lives in QuestPlannerPanel.Update.
    /// </summary>
    public class QuestDetailPanel
    {
        private const float PanelW = 900f;
        private const float HeaderH = 80f;

        private readonly GameObject _root;
        private readonly RectTransform _contentRt;
        private readonly Transform _content;
        private readonly TextMeshProUGUI _title;
        private readonly TextMeshProUGUI _sub;
        private readonly TextMeshProUGUI _message;
        private readonly GameObject _retry;

        private string _questId;
        private List<string> _profiles = new List<string>();

        public bool IsOpen => _root.activeSelf;

        public QuestDetailPanel(Transform canvasRoot)
        {
            _root = QuestPlannerPanel.MakeRect("DetailRoot", canvasRoot);
            QuestPlannerPanel.Stretch(_root.GetComponent<RectTransform>());

            // Dim backdrop; clicking it closes only the detail panel
            var overlay = QuestPlannerPanel.MakeRect("Overlay", _root.transform);
            QuestPlannerPanel.Stretch(overlay.GetComponent<RectTransform>());
            overlay.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);
            var overlayBtn = overlay.AddComponent<Button>();
            overlayBtn.transition = Selectable.Transition.None;
            overlayBtn.onClick.AddListener(Hide);

            var panelGo = QuestPlannerPanel.MakeRect("Panel", _root.transform);
            var panelRt = panelGo.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.08f);
            panelRt.anchorMax = new Vector2(0.5f, 0.92f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(PanelW, 0f);
            panelGo.AddComponent<Image>().color = new Color(0.06f, 0.06f, 0.07f, 1f);
            var panelBlock = panelGo.AddComponent<Button>();
            panelBlock.transition = Selectable.Transition.None;

            var topBar = QuestPlannerPanel.MakeRect("TopBar", panelGo.transform);
            var topBarRt = topBar.GetComponent<RectTransform>();
            topBarRt.anchorMin = new Vector2(0f, 1f);
            topBarRt.anchorMax = new Vector2(1f, 1f);
            topBarRt.pivot = Vector2.up;
            topBarRt.sizeDelta = new Vector2(0f, 3f);
            topBar.AddComponent<Image>().color = QuestPlannerPanel.Accent;

            _title = QuestPlannerPanel.MakeTMP("Title", panelGo.transform, 24f, FontStyles.Bold, TextAlignmentOptions.Left);
            _title.color = QuestPlannerPanel.Accent;
            _title.characterSpacing = 3f;
            _title.overflowMode = TextOverflowModes.Ellipsis;
            _title.enableWordWrapping = false;
            QuestPlannerPanel.SetRect(_title.rectTransform,
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(24f, -HeaderH + 14f), offsetMax: new Vector2(-60f, -14f));

            _sub = QuestPlannerPanel.MakeTMP("Sub", panelGo.transform, 12f, FontStyles.Normal, TextAlignmentOptions.Left);
            _sub.color = new Color(0.35f, 0.35f, 0.35f);
            _sub.characterSpacing = 2f;
            QuestPlannerPanel.SetRect(_sub.rectTransform,
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(24f, -HeaderH + 2f), offsetMax: new Vector2(-60f, -HeaderH + 18f));

            var closeGo = QuestPlannerPanel.MakeRect("Close", panelGo.transform);
            var closeRt = closeGo.GetComponent<RectTransform>();
            closeRt.anchorMin = new Vector2(1f, 1f);
            closeRt.anchorMax = new Vector2(1f, 1f);
            closeRt.pivot = new Vector2(1f, 1f);
            closeRt.anchoredPosition = new Vector2(-12f, -12f);
            closeRt.sizeDelta = new Vector2(32f, 32f);
            closeGo.AddComponent<Image>().color = Color.clear;
            var closeLabel = QuestPlannerPanel.MakeTMP("X", closeGo.transform, 20f, FontStyles.Bold, TextAlignmentOptions.Center);
            QuestPlannerPanel.Stretch(closeLabel.rectTransform);
            closeLabel.text = "✕";
            closeLabel.color = new Color(0.6f, 0.6f, 0.6f);
            var closeBtn = closeGo.AddComponent<Button>();
            closeBtn.transition = Selectable.Transition.None;
            closeBtn.onClick.AddListener(Hide);

            var divider = QuestPlannerPanel.MakeRect("Divider", panelGo.transform);
            var dividerRt = divider.GetComponent<RectTransform>();
            dividerRt.anchorMin = new Vector2(0f, 1f);
            dividerRt.anchorMax = new Vector2(1f, 1f);
            dividerRt.pivot = Vector2.up;
            dividerRt.anchoredPosition = new Vector2(0f, -HeaderH);
            dividerRt.sizeDelta = new Vector2(0f, 1f);
            divider.AddComponent<Image>().color = new Color(
                QuestPlannerPanel.Accent.r, QuestPlannerPanel.Accent.g, QuestPlannerPanel.Accent.b, 0.25f);

            // Scrollable content (same pattern as the planner)
            var scrollGo = QuestPlannerPanel.MakeRect("Scroll", panelGo.transform);
            var scrollRt = scrollGo.GetComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = new Vector2(0f, 12f);
            scrollRt.offsetMax = new Vector2(0f, -(HeaderH + 1f));
            var scrollRect = scrollGo.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.scrollSensitivity = 30f;

            var viewportGo = QuestPlannerPanel.MakeRect("Viewport", scrollGo.transform);
            QuestPlannerPanel.Stretch(viewportGo.GetComponent<RectTransform>());
            viewportGo.AddComponent<RectMask2D>();
            viewportGo.AddComponent<Image>().color = Color.clear; // scroll catch-all
            scrollRect.viewport = viewportGo.GetComponent<RectTransform>();

            var contentGo = QuestPlannerPanel.MakeRect("Content", viewportGo.transform);
            _contentRt = contentGo.GetComponent<RectTransform>();
            _contentRt.anchorMin = new Vector2(0f, 1f);
            _contentRt.anchorMax = new Vector2(1f, 1f);
            _contentRt.pivot = new Vector2(0.5f, 1f);
            var layout = contentGo.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.padding = new RectOffset(24, 24, 8, 16);
            layout.spacing = 6f;
            var fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scrollRect.content = _contentRt;
            _content = contentGo.transform;

            _message = QuestPlannerPanel.MakeTMP("Message", panelGo.transform, 16f, FontStyles.Normal, TextAlignmentOptions.Center);
            _message.color = new Color(0.45f, 0.45f, 0.45f);
            QuestPlannerPanel.SetRect(_message.rectTransform,
                anchorMin: new Vector2(0f, 0.45f), anchorMax: new Vector2(1f, 0.6f),
                offsetMin: new Vector2(24f, 0f), offsetMax: new Vector2(-24f, 0f));
            _message.gameObject.SetActive(false);

            var retryGo = QuestPlannerPanel.MakeRect("Retry", panelGo.transform);
            var retryRt = retryGo.GetComponent<RectTransform>();
            retryRt.anchorMin = new Vector2(0.5f, 0.38f);
            retryRt.anchorMax = new Vector2(0.5f, 0.38f);
            retryRt.pivot = new Vector2(0.5f, 0.5f);
            retryRt.sizeDelta = new Vector2(120f, 32f);
            retryGo.AddComponent<Image>().color = new Color(
                QuestPlannerPanel.Accent.r, QuestPlannerPanel.Accent.g, QuestPlannerPanel.Accent.b, 0.2f);
            var retryLabel = QuestPlannerPanel.MakeTMP("Label", retryGo.transform, 14f, FontStyles.Bold, TextAlignmentOptions.Center);
            QuestPlannerPanel.Stretch(retryLabel.rectTransform);
            retryLabel.text = "RETRY";
            retryLabel.color = QuestPlannerPanel.Accent;
            var retryBtn = retryGo.AddComponent<Button>();
            retryBtn.transition = Selectable.Transition.None;
            retryBtn.onClick.AddListener(Refresh);
            _retry = retryGo;
            _retry.SetActive(false);

            _root.SetActive(false);
        }

        public void ShowFor(string questId, List<string> visibleProfiles)
        {
            _questId = questId;
            _profiles = visibleProfiles ?? new List<string>();
            _root.SetActive(true);
            Refresh();
        }

        public void Hide() => _root.SetActive(false);

        private void Refresh()
        {
            ClearContent();
            ShowMessage("Loading...", showRetry: false);
            _title.text = "QUEST DETAILS";
            _sub.text = "";

            QuestDetailResponse data;
            try
            {
                var response = RequestHandler.GetJson($"/sharedquests/quest/{_questId}");
                data = JsonConvert.DeserializeObject<QuestDetailResponse>(response);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"SharedQuests: Error fetching quest detail: {ex.Message}");
                data = null;
            }

            if (data == null)
            {
                ShowMessage("Couldn't load quest details", showRetry: true);
                return;
            }

            HideMessage();
            Render(data);
        }

        private void Render(QuestDetailResponse data)
        {
            _title.text = (data.Name ?? "").ToUpperInvariant();

            var maps = (data.Maps ?? new List<string>())
                .Select(m => QuestPlannerPanel.MapNames.TryGetValue(m, out var n) ? n : m.ToUpperInvariant())
                .ToList();
            var subParts = new List<string>();
            if (!string.IsNullOrEmpty(data.Trader)) subParts.Add(data.Trader.ToUpperInvariant());
            if (maps.Count > 0) subParts.Add(string.Join(", ", maps));
            subParts.Add("ESC TO GO BACK");
            _sub.text = string.Join("  ·  ", subParts);

            if (!string.IsNullOrEmpty(data.Description))
                AddParagraph(data.Description, 14f, new Color(0.62f, 0.62f, 0.62f));

            var objectives = data.Objectives ?? new List<QuestDetailObjective>();
            if (objectives.Count > 0)
            {
                AddSectionHeader("OBJECTIVES");
                foreach (var objective in objectives)
                {
                    var fragments = _profiles
                        .Select(p => ObjectiveFragment(p, objective))
                        .ToList();
                    var line = $"<color=#CCCCCC>•  {objective.Text}</color>";
                    if (fragments.Count > 0)
                        line += $"\n<line-indent=20>{string.Join("   ", fragments)}</line-indent>";
                    AddParagraph(line, 14f, Color.white);
                }
            }

            var prereqs = data.Prereqs ?? new List<QuestDetailPrereq>();
            if (prereqs.Count > 0)
            {
                AddSectionHeader("PREREQUISITES");
                foreach (var prereq in prereqs)
                {
                    var fragments = _profiles.Select(p =>
                    {
                        var status = 0;
                        if (prereq.Statuses != null) prereq.Statuses.TryGetValue(p, out status);
                        return $"<color={Plugin.GetStatusColor(status)}>{p} {Plugin.GetStatusName(status)}</color>";
                    });
                    AddParagraph($"<color=#CCCCCC>•  {prereq.Name}</color>   {string.Join("   ", fragments)}",
                        14f, Color.white);
                }
            }

            var rewards = data.Rewards ?? new List<string>();
            if (rewards.Count > 0)
            {
                AddSectionHeader("REWARDS");
                AddParagraph(string.Join("\n", rewards.Select(r => $"•  {r}")), 14f, new Color(0.62f, 0.62f, 0.62f));
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_contentRt);
        }

        private static string ObjectiveFragment(string profile, QuestDetailObjective objective)
        {
            QuestDetailObjectiveProgress progress = null;
            if (objective.Progress != null) objective.Progress.TryGetValue(profile, out progress);
            if (progress != null && progress.Done)
                return $"<color=#32CD32>{profile} ✓</color>";
            if (progress != null && progress.Count.HasValue)
            {
                var counter = objective.Target.HasValue
                    ? $"{progress.Count.Value}/{(int)objective.Target.Value}"
                    : progress.Count.Value.ToString();
                return $"<color=#FFA500>{profile} {counter}</color>";
            }
            return $"<color=#555555>{profile} –</color>";
        }

        private void AddSectionHeader(string text)
        {
            var label = QuestPlannerPanel.MakeTMP("Section", _content, 13f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            label.text = text;
            label.color = QuestPlannerPanel.Accent;
            label.characterSpacing = 3f;
            var le = label.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = 30f;
            le.flexibleHeight = 0f;
        }

        private void AddParagraph(string text, float fontSize, Color color)
        {
            var label = QuestPlannerPanel.MakeTMP("Para", _content, fontSize, FontStyles.Normal, TextAlignmentOptions.TopLeft);
            label.text = text;
            label.color = color;
            label.enableWordWrapping = true;
        }

        private void ShowMessage(string text, bool showRetry)
        {
            _message.text = text;
            _message.gameObject.SetActive(true);
            _retry.SetActive(showRetry);
        }

        private void HideMessage()
        {
            _message.gameObject.SetActive(false);
            _retry.SetActive(false);
        }

        private void ClearContent()
        {
            for (int i = _content.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_content.GetChild(i).gameObject);
        }
    }
}
