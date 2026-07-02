using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using SPT.Common.Http;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SharedQuests
{
    /// <summary>Overview payload from /sharedquests/overview (mirrors server DTOs).</summary>
    public class OverviewProfileStatus
    {
        public int Status { get; set; }
        public string LockedReason { get; set; }
    }

    public class OverviewQuest
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Trader { get; set; }
        public List<string> Maps { get; set; }
        public Dictionary<string, OverviewProfileStatus> Statuses { get; set; }
    }

    public class OverviewResponse
    {
        public List<string> Profiles { get; set; }
        public List<OverviewQuest> Quests { get; set; }
    }

    /// <summary>
    /// Full-screen overlay showing all profiles' quest statuses grouped by map.
    /// UI is built entirely in code (no asset bundles), LootNet-style.
    /// </summary>
    public class QuestPlannerPanel : MonoBehaviour
    {
        private static QuestPlannerPanel _instance;
        public static QuestPlannerPanel Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("SharedQuestsPlanner");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<QuestPlannerPanel>();
                }
                return _instance;
            }
        }

        public static readonly Color Accent = new Color(0.604f, 0.533f, 0.400f); // #9A8866
        private const float HeaderH = 80f;
        private const float PanelW = 1360f;

        private GameObject _root;
        private RectTransform _contentRt;
        private Transform _contentContainer;
        private ScrollRect _scrollRect;
        private TextMeshProUGUI _messageLabel;
        private GameObject _retryButton;
        private bool _visible;
        private QuestDetailPanel _detail;

        // canonical map id (from server) -> display name
        internal static readonly Dictionary<string, string> MapNames = new Dictionary<string, string>
        {
            ["bigmap"] = "CUSTOMS",
            ["factory"] = "FACTORY",
            ["interchange"] = "INTERCHANGE",
            ["laboratory"] = "THE LAB",
            ["lighthouse"] = "LIGHTHOUSE",
            ["rezervbase"] = "RESERVE",
            ["sandbox"] = "GROUND ZERO",
            ["shoreline"] = "SHORELINE",
            ["tarkovstreets"] = "STREETS OF TARKOV",
            ["woods"] = "WOODS",
            ["labyrinth"] = "LABYRINTH",
            ["suburbs"] = "SUBURBS",
            ["terminal"] = "TERMINAL",
            ["town"] = "TOWN",
        };

        private const string AnyMapKey = "__any__";
        private const float PlayerColW = 130f;
        private const float RowH = 32f;
        private const float SectionHeaderH = 42f;
        private const float FilterH = 44f;
        private const float StickyH = 36f;

        // map id -> expanded state, persists across refreshes while the game runs
        private readonly Dictionary<string, bool> _sectionExpanded = new Dictionary<string, bool>();

        // filter state; cached response so filtering rebuilds locally without a refetch
        private OverviewResponse _lastData;
        private string _search = "";
        private bool _sharedOnly;
        private string _traderFilter; // null = all
        private List<string> _traders = new List<string>();
        private TMP_InputField _searchInput;
        private TextMeshProUGUI _traderLabel;
        private TextMeshProUGUI _sharedLabel;
        private Transform _stickyHeader;

        private static readonly Color ButtonTextColor = new Color(0.7f, 0.7f, 0.7f);
        private static readonly Color SharedGold = new Color(1f, 0.647f, 0f); // #FFA500

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            BuildUI();
        }

        private void Update()
        {
            if (!_visible || !Input.GetKeyDown(KeyCode.Escape)) return;
            if (_searchInput != null && _searchInput.isFocused) return; // Esc just unfocuses the search box
            if (_detail != null && _detail.IsOpen) _detail.Hide();
            else Hide();
        }

        public void Toggle()
        {
            if (_visible) Hide(); else Show();
        }

        public void Show()
        {
            if (_visible) return;
            _root.SetActive(true);
            _visible = true;
            RefreshContent();
            if (_scrollRect != null) _scrollRect.verticalNormalizedPosition = 1f;
        }

        public void Hide()
        {
            if (!_visible) return;
            _visible = false;
            _detail?.Hide();
            _root.SetActive(false);
        }

        /// <summary>Fetches overview data, then rebuilds the rows.</summary>
        private void RefreshContent()
        {
            ShowMessage("Loading...", showRetry: false);

            try
            {
                var response = RequestHandler.GetJson("/sharedquests/overview");
                _lastData = JsonConvert.DeserializeObject<OverviewResponse>(response);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"SharedQuests: Error fetching overview: {ex.Message}");
                _lastData = null;
            }

            if (_lastData == null || _lastData.Profiles == null || _lastData.Quests == null)
            {
                _lastData = null;
                ShowMessage("Couldn't reach SharedQuests server", showRetry: true);
                return;
            }

            // Keep the F12 profile checkboxes in sync
            Settings.UpdateProfileList(_lastData.Profiles);

            Rebuild();
        }

        /// <summary>Re-filters the cached overview and rebuilds all rows (no network).</summary>
        private void Rebuild()
        {
            if (_lastData == null) return;

            var visibleProfiles = _lastData.Profiles.Where(Settings.IsProfileVisible).ToList();
            if (visibleProfiles.Count == 0)
            {
                ShowMessage("No profiles selected (check F12 menu)", showRetry: false);
                return;
            }

            // Re-apply relevance for visible profiles only: a quest active solely
            // for excluded profiles is hidden entirely.
            bool IsActive(OverviewQuest q, string profile) =>
                q.Statuses != null && q.Statuses.TryGetValue(profile, out var s) && (s.Status == 1 || s.Status == 2 || s.Status == 3);
            var relevant = _lastData.Quests
                .Where(q => visibleProfiles.Any(p => IsActive(q, p)))
                .ToList();

            if (relevant.Count == 0)
            {
                ShowMessage("No active quests found", showRetry: false);
                return;
            }

            // Trader options come from the unfiltered list so cycling never shrinks
            _traders = relevant.Select(q => q.Trader)
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();

            bool filtersActive = _search.Length > 0 || _sharedOnly || _traderFilter != null;
            var filtered = relevant.Where(q =>
                    (_search.Length == 0
                        || (q.Name != null && q.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (q.Trader != null && q.Trader.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0))
                    && (_traderFilter == null || q.Trader == _traderFilter)
                    && (!_sharedOnly || IsSharedByAll(q, visibleProfiles)))
                .ToList();

            if (filtered.Count == 0)
            {
                ShowMessage("No quests match the current filters", showRetry: false);
                BuildStickyHeader(visibleProfiles);
                return;
            }

            // Group by map (a multi-map quest appears under each map; no maps -> "any map")
            var groups = new Dictionary<string, List<OverviewQuest>>();
            foreach (var quest in filtered)
            {
                var keys = (quest.Maps != null && quest.Maps.Count > 0) ? quest.Maps : new List<string> { AnyMapKey };
                foreach (var key in keys)
                {
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<OverviewQuest>();
                    list.Add(quest);
                }
            }

            // Sort: most players with active quests desc, then quest count desc; "any map" last
            var ordered = groups
                .OrderBy(g => g.Key == AnyMapKey ? 1 : 0)
                .ThenByDescending(g => visibleProfiles.Count(p => g.Value.Any(q => IsActive(q, p))))
                .ThenByDescending(g => g.Value.Count)
                .ToList();

            HideMessage();
            ClearContent();
            BuildStickyHeader(visibleProfiles);
            foreach (var group in ordered)
                BuildMapSection(group.Key, group.Value, visibleProfiles, IsActive, filtersActive);

            LayoutRebuilder.ForceRebuildLayoutImmediate(_contentRt);
            if (_scrollRect != null) _scrollRect.verticalNormalizedPosition = 1f;
        }

        /// <summary>Show a centered message (loading / error / empty) instead of rows.</summary>
        private void ShowMessage(string text, bool showRetry)
        {
            ClearContent();
            ClearStickyHeader();
            _messageLabel.text = text;
            _messageLabel.gameObject.SetActive(true);
            _retryButton.SetActive(showRetry);
        }

        private void HideMessage()
        {
            _messageLabel.gameObject.SetActive(false);
            _retryButton.SetActive(false);
        }

        private void ClearContent()
        {
            for (int i = _contentContainer.childCount - 1; i >= 0; i--)
                Destroy(_contentContainer.GetChild(i).gameObject);
        }

        private void BuildUI()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            gameObject.AddComponent<GraphicRaycaster>();

            _root = MakeRect("PlannerRoot", transform);
            Stretch(_root.GetComponent<RectTransform>());

            // Dim backdrop; clicking it closes the panel
            var overlay = MakeRect("Overlay", _root.transform);
            Stretch(overlay.GetComponent<RectTransform>());
            overlay.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);
            var overlayBtn = overlay.AddComponent<Button>();
            overlayBtn.transition = Selectable.Transition.None;
            overlayBtn.onClick.AddListener(Hide);

            // Centered panel, 8%..92% of screen height
            var panelGo = MakeRect("Panel", _root.transform);
            var panelRt = panelGo.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.08f);
            panelRt.anchorMax = new Vector2(0.5f, 0.92f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(PanelW, 0f);
            panelGo.AddComponent<Image>().color = new Color(0.06f, 0.06f, 0.07f, 1f);

            // Block backdrop clicks under the panel
            var panelBlock = panelGo.AddComponent<Button>();
            panelBlock.transition = Selectable.Transition.None;

            var topBar = MakeRect("TopBar", panelGo.transform);
            var topBarRt = topBar.GetComponent<RectTransform>();
            topBarRt.anchorMin = new Vector2(0f, 1f);
            topBarRt.anchorMax = new Vector2(1f, 1f);
            topBarRt.pivot = Vector2.up;
            topBarRt.sizeDelta = new Vector2(0f, 3f);
            topBar.AddComponent<Image>().color = Accent;

            var title = MakeTMP("Title", panelGo.transform, 26f, FontStyles.Bold, TextAlignmentOptions.Left);
            title.text = "SHARED QUESTS";
            title.color = Accent;
            title.characterSpacing = 5f;
            SetRect(title.rectTransform,
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(24f, -HeaderH + 14f), offsetMax: new Vector2(-60f, -14f));

            var sub = MakeTMP("Sub", panelGo.transform, 12f, FontStyles.Normal, TextAlignmentOptions.Left);
            sub.text = "QUEST PROGRESS BY MAP  ·  ESC OR CLICK OUTSIDE TO CLOSE";
            sub.color = new Color(0.35f, 0.35f, 0.35f);
            sub.characterSpacing = 2f;
            SetRect(sub.rectTransform,
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(24f, -HeaderH + 2f), offsetMax: new Vector2(-60f, -HeaderH + 18f));

            // Close button (✕)
            var closeGo = MakeRect("Close", panelGo.transform);
            var closeRt = closeGo.GetComponent<RectTransform>();
            closeRt.anchorMin = new Vector2(1f, 1f);
            closeRt.anchorMax = new Vector2(1f, 1f);
            closeRt.pivot = new Vector2(1f, 1f);
            closeRt.anchoredPosition = new Vector2(-12f, -12f);
            closeRt.sizeDelta = new Vector2(32f, 32f);
            closeGo.AddComponent<Image>().color = Color.clear;
            var closeLabel = MakeTMP("X", closeGo.transform, 20f, FontStyles.Bold, TextAlignmentOptions.Center);
            Stretch(closeLabel.rectTransform);
            closeLabel.text = "✕";
            closeLabel.color = new Color(0.6f, 0.6f, 0.6f);
            var closeBtn = closeGo.AddComponent<Button>();
            closeBtn.transition = Selectable.Transition.None;
            closeBtn.onClick.AddListener(Hide);

            // Filter bar: search (stretches) + trader cycle + shared-only + clear
            var bar = MakeRect("FilterBar", panelGo.transform);
            SetRect(bar.GetComponent<RectTransform>(),
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(24f, -(HeaderH + FilterH) + 6f), offsetMax: new Vector2(-24f, -HeaderH - 6f));
            var barLayout = bar.AddComponent<HorizontalLayoutGroup>();
            barLayout.childControlWidth = true;
            barLayout.childControlHeight = true;
            barLayout.childForceExpandWidth = false;
            barLayout.childForceExpandHeight = true;
            barLayout.spacing = 8f;

            _searchInput = MakeSearchInput(bar.transform);
            // ponytail: cycle button instead of a real TMP_Dropdown — a code-built dropdown
            // template isn't worth it; revisit if trader count grows past ~10
            _traderLabel = MakeFilterButton(bar.transform, "TRADER: ALL", 180f, CycleTrader);
            _sharedLabel = MakeFilterButton(bar.transform, "SHARED ONLY", 130f, ToggleSharedOnly);
            MakeFilterButton(bar.transform, "CLEAR", 80f, ClearFilters);

            var divider = MakeRect("Divider", panelGo.transform);
            var dividerRt = divider.GetComponent<RectTransform>();
            dividerRt.anchorMin = new Vector2(0f, 1f);
            dividerRt.anchorMax = new Vector2(1f, 1f);
            dividerRt.pivot = Vector2.up;
            dividerRt.anchoredPosition = new Vector2(0f, -(HeaderH + FilterH));
            dividerRt.sizeDelta = new Vector2(0f, 1f);
            divider.AddComponent<Image>().color = new Color(Accent.r, Accent.g, Accent.b, 0.25f);

            // Sticky player-name header: fixed above the scroll viewport, mirrors row layout
            var stickyGo = MakeRect("StickyHeader", panelGo.transform);
            var stickyRt = stickyGo.GetComponent<RectTransform>();
            stickyRt.anchorMin = new Vector2(0f, 1f);
            stickyRt.anchorMax = new Vector2(1f, 1f);
            stickyRt.pivot = Vector2.up;
            stickyRt.anchoredPosition = new Vector2(0f, -(HeaderH + FilterH + 1f));
            stickyRt.sizeDelta = new Vector2(0f, StickyH);
            var stickyLayout = stickyGo.AddComponent<HorizontalLayoutGroup>();
            stickyLayout.childControlWidth = true;
            stickyLayout.childControlHeight = true;
            stickyLayout.childForceExpandWidth = false;
            stickyLayout.childForceExpandHeight = true;
            stickyLayout.spacing = 4f;
            stickyLayout.padding = new RectOffset(24, 24, 0, 0);
            _stickyHeader = stickyGo.transform;

            // Scrollable content
            var scrollGo = MakeRect("Scroll", panelGo.transform);
            var scrollRt = scrollGo.GetComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = new Vector2(0f, 12f);
            scrollRt.offsetMax = new Vector2(0f, -(HeaderH + FilterH + 1f + StickyH));
            _scrollRect = scrollGo.AddComponent<ScrollRect>();
            _scrollRect.horizontal = false;
            _scrollRect.scrollSensitivity = 30f;

            var viewportGo = MakeRect("Viewport", scrollGo.transform);
            Stretch(viewportGo.GetComponent<RectTransform>());
            viewportGo.AddComponent<RectMask2D>();
            // invisible catch-all so scroll events over any row reach the ScrollRect
            var viewportCatcher = viewportGo.AddComponent<Image>();
            viewportCatcher.color = Color.clear;
            _scrollRect.viewport = viewportGo.GetComponent<RectTransform>();

            var contentGo = MakeRect("Content", viewportGo.transform);
            _contentRt = contentGo.GetComponent<RectTransform>();
            _contentRt.anchorMin = new Vector2(0f, 1f);
            _contentRt.anchorMax = new Vector2(1f, 1f);
            _contentRt.pivot = new Vector2(0.5f, 1f);
            _contentRt.anchoredPosition = Vector2.zero;
            _contentRt.sizeDelta = Vector2.zero;
            var layout = contentGo.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.padding = new RectOffset(24, 24, 4, 16);
            layout.spacing = 2f;
            var fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _scrollRect.content = _contentRt;
            _contentContainer = contentGo.transform;

            // Centered message + retry (for loading/error/empty states)
            _messageLabel = MakeTMP("Message", panelGo.transform, 16f, FontStyles.Normal, TextAlignmentOptions.Center);
            _messageLabel.color = new Color(0.45f, 0.45f, 0.45f);
            SetRect(_messageLabel.rectTransform,
                anchorMin: new Vector2(0f, 0.45f), anchorMax: new Vector2(1f, 0.6f),
                offsetMin: new Vector2(24f, 0f), offsetMax: new Vector2(-24f, 0f));
            _messageLabel.gameObject.SetActive(false);

            var retryGo = MakeRect("Retry", panelGo.transform);
            var retryRt = retryGo.GetComponent<RectTransform>();
            retryRt.anchorMin = new Vector2(0.5f, 0.38f);
            retryRt.anchorMax = new Vector2(0.5f, 0.38f);
            retryRt.pivot = new Vector2(0.5f, 0.5f);
            retryRt.sizeDelta = new Vector2(120f, 32f);
            retryGo.AddComponent<Image>().color = new Color(Accent.r, Accent.g, Accent.b, 0.2f);
            var retryLabel = MakeTMP("Label", retryGo.transform, 14f, FontStyles.Bold, TextAlignmentOptions.Center);
            Stretch(retryLabel.rectTransform);
            retryLabel.text = "RETRY";
            retryLabel.color = Accent;
            var retryBtn = retryGo.AddComponent<Button>();
            retryBtn.transition = Selectable.Transition.None;
            retryBtn.onClick.AddListener(RefreshContent);
            _retryButton = retryGo;
            _retryButton.SetActive(false);

            _root.SetActive(false);

            _detail = new QuestDetailPanel(transform);
        }

        /// <summary>Fills the sticky header: blank name column + one profile name per column.</summary>
        private void BuildStickyHeader(List<string> profiles)
        {
            ClearStickyHeader();
            AddCell(_stickyHeader, "", flexible: true, 15f, FontStyles.Bold, Color.clear);
            foreach (var profile in profiles)
            {
                var cell = AddCell(_stickyHeader, profile, flexible: false, 15f, FontStyles.Bold,
                    new Color(0.8f, 0.8f, 0.8f));
                cell.overflowMode = TextOverflowModes.Ellipsis;
            }
        }

        private void ClearStickyHeader()
        {
            if (_stickyHeader == null) return;
            for (int i = _stickyHeader.childCount - 1; i >= 0; i--)
                Destroy(_stickyHeader.GetChild(i).gameObject);
        }

        /// <summary>Search box: bg image + TMP_InputField with masked text area and placeholder.</summary>
        private TMP_InputField MakeSearchInput(Transform parent)
        {
            var go = MakeRect("Search", parent);
            go.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.06f);
            go.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var input = go.AddComponent<TMP_InputField>();
            input.transition = Selectable.Transition.None;

            var area = MakeRect("TextArea", go.transform);
            var areaRt = area.GetComponent<RectTransform>();
            Stretch(areaRt);
            areaRt.offsetMin = new Vector2(10f, 2f);
            areaRt.offsetMax = new Vector2(-10f, -2f);
            area.AddComponent<RectMask2D>();

            var placeholder = MakeTMP("Placeholder", area.transform, 14f, FontStyles.Italic, TextAlignmentOptions.MidlineLeft);
            Stretch(placeholder.rectTransform);
            placeholder.text = "Search quests...";
            placeholder.color = new Color(0.4f, 0.4f, 0.4f);

            var text = MakeTMP("Text", area.transform, 14f, FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            Stretch(text.rectTransform);
            text.color = new Color(0.85f, 0.85f, 0.85f);

            input.textViewport = areaRt;
            input.textComponent = text;
            input.placeholder = placeholder;
            input.customCaretColor = true;
            input.caretColor = new Color(0.85f, 0.85f, 0.85f);
            input.selectionColor = new Color(Accent.r, Accent.g, Accent.b, 0.4f);
            input.onValueChanged.AddListener(v => { _search = v ?? ""; Rebuild(); });
            return input;
        }

        /// <summary>Fixed-width filter-bar button; returns the label so callers can restyle it.</summary>
        private TextMeshProUGUI MakeFilterButton(Transform parent, string text, float width, Action onClick)
        {
            var go = MakeRect(text, parent);
            go.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.06f);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.flexibleWidth = 0f;
            var label = MakeTMP("Label", go.transform, 13f, FontStyles.Bold, TextAlignmentOptions.Center);
            Stretch(label.rectTransform);
            label.text = text;
            label.color = ButtonTextColor;
            var btn = go.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => onClick());
            return label;
        }

        private void CycleTrader()
        {
            if (_traders.Count == 0) return;
            int idx = (_traderFilter == null ? -1 : _traders.IndexOf(_traderFilter)) + 1;
            _traderFilter = idx >= _traders.Count ? null : _traders[idx];
            _traderLabel.text = _traderFilter == null ? "TRADER: ALL" : $"TRADER: {_traderFilter.ToUpperInvariant()}";
            _traderLabel.color = _traderFilter == null ? ButtonTextColor : Accent;
            Rebuild();
        }

        private void ToggleSharedOnly()
        {
            _sharedOnly = !_sharedOnly;
            _sharedLabel.color = _sharedOnly ? SharedGold : ButtonTextColor;
            Rebuild();
        }

        private void ClearFilters()
        {
            _search = "";
            _searchInput.SetTextWithoutNotify("");
            _sharedOnly = false;
            _sharedLabel.color = ButtonTextColor;
            _traderFilter = null;
            _traderLabel.text = "TRADER: ALL";
            _traderLabel.color = ButtonTextColor;
            Rebuild();
        }

        private void BuildMapSection(string mapKey, List<OverviewQuest> quests,
            List<string> profiles, Func<OverviewQuest, string, bool> isActive, bool forceExpanded)
        {
            string displayName = mapKey == AnyMapKey
                ? "ANY MAP"
                : (MapNames.TryGetValue(mapKey, out var n) ? n : mapKey.ToUpperInvariant());
            int playerCount = profiles.Count(p => quests.Any(q => isActive(q, p)));

            // Everything starts collapsed; active filters force sections open so hits are visible
            if (!_sectionExpanded.TryGetValue(mapKey, out var expanded))
                _sectionExpanded[mapKey] = expanded = false;
            if (forceExpanded) expanded = true;

            // Section header (click to toggle)
            var headerGo = MakeRow($"Section_{mapKey}", SectionHeaderH);
            headerGo.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.04f);
            var headerLabel = MakeTMP("Label", headerGo.transform, 17f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            Stretch(headerLabel.rectTransform);
            headerLabel.rectTransform.offsetMin = new Vector2(8f, 0f);
            string arrow = expanded ? "▼" : "▶";
            string plural = playerCount == 1 ? "player" : "players";
            string questPlural = quests.Count == 1 ? "quest" : "quests";
            int sharedCount = quests.Count(q => IsSharedByAll(q, profiles));
            headerLabel.text =
                $"<color=#9A8866>{arrow}  {displayName}</color>" +
                $"<color=#666666>   {playerCount} {plural} · {quests.Count} {questPlural}</color>" +
                (sharedCount > 0 ? $"<color=#FFA500> · {sharedCount} shared</color>" : "");

            // Rows container so toggling is a single SetActive
            var rowsGo = MakeRect($"Rows_{mapKey}", _contentContainer);
            var rowsLayout = rowsGo.AddComponent<VerticalLayoutGroup>();
            rowsLayout.childControlWidth = true;
            rowsLayout.childControlHeight = true;
            rowsLayout.childForceExpandWidth = true;
            rowsLayout.childForceExpandHeight = false;
            rowsLayout.spacing = 1f;
            rowsGo.SetActive(expanded);

            var headerBtn = headerGo.AddComponent<Button>();
            headerBtn.transition = Selectable.Transition.None;
            headerBtn.onClick.AddListener(() =>
            {
                bool now = !rowsGo.activeSelf; // displayed state, not stored (filters can force open)
                _sectionExpanded[mapKey] = now;
                rowsGo.SetActive(now);
                headerLabel.text = headerLabel.text.Replace(now ? "▶" : "▼", now ? "▼" : "▶");
                if (now)
                    LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)rowsGo.transform);
                LayoutRebuilder.ForceRebuildLayoutImmediate(_contentRt);
            });

            foreach (var quest in quests.OrderBy(q => q.Name, StringComparer.Ordinal))
                BuildQuestRow(rowsGo.transform, quest, profiles);
        }

        /// <summary>Active-for-everyone: each profile can start (1) or has started (2) the quest.</summary>
        private static bool IsSharedByAll(OverviewQuest quest, List<string> profiles)
        {
            if (quest.Statuses == null) return false;
            foreach (var profile in profiles)
            {
                if (!quest.Statuses.TryGetValue(profile, out var s)) return false;
                if (s.Status != 1 && s.Status != 2) return false;
            }
            return profiles.Count > 0;
        }

        private void BuildQuestRow(Transform parent, OverviewQuest quest, List<string> profiles)
        {
            var row = MakeRow("Quest", RowH, parent);
            row.AddComponent<Image>().color = Color.clear; // raycast target for the button
            var rowBtn = row.AddComponent<Button>();
            rowBtn.transition = Selectable.Transition.None;
            var questId = quest.Id;
            rowBtn.onClick.AddListener(() => _detail.ShowFor(questId, profiles));

            string traderSuffix = string.IsNullOrEmpty(quest.Trader) ? "" : $"  <color=#555555>{quest.Trader}</color>";
            string nameColor = IsSharedByAll(quest, profiles) ? "#FFA500" : "#CCCCCC";
            var nameCell = AddCell(row.transform, $"<color={nameColor}>{quest.Name}</color>{traderSuffix}",
                flexible: true, 15f, FontStyles.Normal, Color.white);
            nameCell.overflowMode = TextOverflowModes.Ellipsis;

            foreach (var profile in profiles)
            {
                OverviewProfileStatus info = null;
                if (quest.Statuses != null) quest.Statuses.TryGetValue(profile, out info);
                int status = info != null ? info.Status : 0;
                // Locked with no known blocker = quest just isn't relevant to this profile yet
                bool notRelevant = status == 0 && (info == null || string.IsNullOrEmpty(info.LockedReason));
                var cell = AddCell(row.transform, "", flexible: false, 14f, FontStyles.Bold, Color.white);
                cell.text = notRelevant
                    ? "<color=#555555>–</color>"
                    : $"<color={Plugin.GetStatusColor(status)}>{Plugin.GetStatusName(status)}</color>";
            }

            // One indented sub-row per blocked profile with a known reason
            foreach (var profile in profiles)
            {
                if (quest.Statuses == null || !quest.Statuses.TryGetValue(profile, out var info)) continue;
                if (info.Status != 0 || string.IsNullOrEmpty(info.LockedReason)) continue;
                var subRow = MakeRow("Blocked", RowH - 6f, parent);
                var subLabel = AddCell(subRow.transform,
                    $"<color=#666666>└ {profile} needs: {info.LockedReason}</color>",
                    flexible: true, 13f, FontStyles.Normal, Color.white);
                subLabel.rectTransform.offsetMin = new Vector2(24f, 0f);
                subLabel.overflowMode = TextOverflowModes.Ellipsis;
            }
        }

        /// <summary>A fixed-height row with a horizontal layout, parented to content by default.</summary>
        private GameObject MakeRow(string name, float height, Transform parent = null)
        {
            var go = MakeRect(name, parent ?? _contentContainer);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleHeight = 0f;
            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            layout.spacing = 4f;
            return go;
        }

        private TextMeshProUGUI AddCell(Transform row, string text, bool flexible,
            float fontSize, FontStyles style, Color color)
        {
            var label = MakeTMP("Cell", row, fontSize, style,
                flexible ? TextAlignmentOptions.MidlineLeft : TextAlignmentOptions.Midline);
            label.text = text;
            label.color = color;
            var le = label.gameObject.AddComponent<LayoutElement>();
            if (flexible) { le.flexibleWidth = 1f; le.minWidth = 280f; }
            else { le.preferredWidth = PlayerColW; le.flexibleWidth = 0f; }
            return label;
        }

        // --- small builders (LootNet pattern) ---

        internal static GameObject MakeRect(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            return go;
        }

        internal static TextMeshProUGUI MakeTMP(string name, Transform parent,
            float size, FontStyles style, TextAlignmentOptions align)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = align;
            t.richText = true;
            return t;
        }

        internal static void SetRect(RectTransform rt,
            Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin; rt.offsetMax = offsetMax;
        }

        internal static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero; rt.anchoredPosition = Vector2.zero;
        }
    }
}
