using UnityEngine;
using UnityEngine.UI;
using CupheadOnline.Sync;

namespace CupheadOnline.UI
{
    public sealed class SessionPausePanel : MonoBehaviour
    {
        private static readonly Color TitleColour = new Color(0.95f, 0.86f, 0.42f, 1f);
        private static readonly Color BodyColour = new Color(0.94f, 0.91f, 0.82f, 1f);
        private static readonly Color HintColour = new Color(0.72f, 0.70f, 0.66f, 0.92f);

        public static SessionPausePanel Instance { get; private set; }

        private CanvasGroup _canvasGroup;
        private Image _bg;
        private Text _title;
        private Text _body;
        private Text _hint;
        private bool _manualVisible;

        public static void Ensure()
        {
            if (Instance != null) return;

            var go = new GameObject("CupheadOnline_SessionPanel");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<SessionPausePanel>();
        }

        private void Awake()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 160;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            gameObject.AddComponent<GraphicRaycaster>();

            var panel = new GameObject("SessionPanel");
            panel.transform.SetParent(transform, false);

            var rt = panel.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.anchoredPosition = new Vector2(-36f, 0f);
            rt.sizeDelta = new Vector2(420f, 236f);

            _bg = panel.AddComponent<Image>();
            CupheadUiTheme.StyleCard(_bg, dark: true);

            _canvasGroup = panel.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 0f;
            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = false;

            _title = MakeText(panel, "~ SESSION ~", 18, CupheadUiTheme.Gold, new Vector2(0f, 86f), new Vector2(380f, 24f), TextAnchor.MiddleCenter);
            _body = MakeText(panel, string.Empty, 12, BodyColour, new Vector2(0f, -4f), new Vector2(380f, 156f), TextAnchor.UpperLeft);
            _body.horizontalOverflow = HorizontalWrapMode.Wrap;
            _body.verticalOverflow = VerticalWrapMode.Overflow;
            _body.lineSpacing = 1.1f;

            _hint = MakeText(panel, "[ F8 toggles this panel ]", 10, HintColour, new Vector2(0f, -92f), new Vector2(380f, 20f), TextAnchor.MiddleCenter);
        }

        private void Update()
        {
            CupheadUiTheme.RefreshLabelFonts(gameObject);

            if (Input.GetKeyDown(KeyCode.F8))
                _manualVisible = !_manualVisible;

            bool shouldShow = Plugin.ShowPauseSessionPanel
                           && MultiplayerSession.IsActive
                           && (_manualVisible || PauseManager.state == PauseManager.State.Paused);

            if (_canvasGroup != null)
            {
                _canvasGroup.alpha = shouldShow ? 1f : 0f;
                _canvasGroup.interactable = false;
                _canvasGroup.blocksRaycasts = false;
            }

            if (!shouldShow || _body == null) return;

            _body.text = SessionSync.BuildPausePanelText();
            _body.color = SessionSync.GetSeverityColor(
                SessionSync.DesyncSeverity >= SessionSync.CompatibilitySeverity
                    ? SessionSync.DesyncSeverity
                    : SessionSync.CompatibilitySeverity);

            if (SessionSync.DesyncSeverity == SessionIssueSeverity.None
             && SessionSync.CompatibilitySeverity == SessionIssueSeverity.None)
            {
                _body.color = BodyColour;
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private static Text MakeText(GameObject parent, string content, int size, Color color, Vector2 offset, Vector2 sizeDelta, TextAnchor anchor)
        {
            return CupheadUiTheme.MakeLabel(
                parent, content, size, color, offset, sizeDelta,
                anchor, new Vector2(0.5f, 0.5f));
        }
    }
}
