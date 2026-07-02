using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SharedQuests
{
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
        private const float HeaderH = 72f;
        private const float PanelW = 980f;

        private GameObject _root;
        private RectTransform _contentRt;
        private Transform _contentContainer;
        private ScrollRect _scrollRect;
        private TextMeshProUGUI _messageLabel;
        private GameObject _retryButton;
        private bool _visible;

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            BuildUI();
        }

        private void Update()
        {
            if (_visible && Input.GetKeyDown(KeyCode.Escape)) Hide();
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
            _root.SetActive(false);
        }

        /// <summary>Fetches overview data and rebuilds the rows. Filled in by the data task.</summary>
        private void RefreshContent()
        {
            ShowMessage("Loading...", showRetry: false);
        }

        /// <summary>Show a centered message (loading / error / empty) instead of rows.</summary>
        private void ShowMessage(string text, bool showRetry)
        {
            ClearContent();
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
            gameObject.AddComponent<CanvasScaler>();
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

            var title = MakeTMP("Title", panelGo.transform, 22f, FontStyles.Bold, TextAlignmentOptions.Left);
            title.text = "SHARED QUESTS";
            title.color = Accent;
            title.characterSpacing = 5f;
            SetRect(title.rectTransform,
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(24f, -HeaderH + 14f), offsetMax: new Vector2(-60f, -14f));

            var sub = MakeTMP("Sub", panelGo.transform, 10f, FontStyles.Normal, TextAlignmentOptions.Left);
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
            var closeLabel = MakeTMP("X", closeGo.transform, 18f, FontStyles.Bold, TextAlignmentOptions.Center);
            Stretch(closeLabel.rectTransform);
            closeLabel.text = "✕";
            closeLabel.color = new Color(0.6f, 0.6f, 0.6f);
            var closeBtn = closeGo.AddComponent<Button>();
            closeBtn.transition = Selectable.Transition.None;
            closeBtn.onClick.AddListener(Hide);

            var divider = MakeRect("Divider", panelGo.transform);
            var dividerRt = divider.GetComponent<RectTransform>();
            dividerRt.anchorMin = new Vector2(0f, 1f);
            dividerRt.anchorMax = new Vector2(1f, 1f);
            dividerRt.pivot = Vector2.up;
            dividerRt.anchoredPosition = new Vector2(0f, -HeaderH);
            dividerRt.sizeDelta = new Vector2(0f, 1f);
            divider.AddComponent<Image>().color = new Color(Accent.r, Accent.g, Accent.b, 0.25f);

            // Scrollable content
            var scrollGo = MakeRect("Scroll", panelGo.transform);
            var scrollRt = scrollGo.GetComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = new Vector2(0f, 12f);
            scrollRt.offsetMax = new Vector2(0f, -(HeaderH + 1f));
            _scrollRect = scrollGo.AddComponent<ScrollRect>();
            _scrollRect.horizontal = false;
            _scrollRect.scrollSensitivity = 30f;

            var viewportGo = MakeRect("Viewport", scrollGo.transform);
            Stretch(viewportGo.GetComponent<RectTransform>());
            viewportGo.AddComponent<RectMask2D>();
            _scrollRect.viewport = viewportGo.GetComponent<RectTransform>();

            var contentGo = MakeRect("Content", viewportGo.transform);
            _contentRt = contentGo.GetComponent<RectTransform>();
            _contentRt.anchorMin = new Vector2(0f, 1f);
            _contentRt.anchorMax = new Vector2(1f, 1f);
            _contentRt.pivot = new Vector2(0.5f, 1f);
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
            _messageLabel = MakeTMP("Message", panelGo.transform, 14f, FontStyles.Normal, TextAlignmentOptions.Center);
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
            var retryLabel = MakeTMP("Label", retryGo.transform, 12f, FontStyles.Bold, TextAlignmentOptions.Center);
            Stretch(retryLabel.rectTransform);
            retryLabel.text = "RETRY";
            retryLabel.color = Accent;
            var retryBtn = retryGo.AddComponent<Button>();
            retryBtn.transition = Selectable.Transition.None;
            retryBtn.onClick.AddListener(RefreshContent);
            _retryButton = retryGo;
            _retryButton.SetActive(false);

            _root.SetActive(false);
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
