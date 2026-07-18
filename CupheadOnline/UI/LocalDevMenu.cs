using CupheadOnline.Sync;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CupheadOnline.UI
{
    /// <summary>
    /// F11 developer lab for same-PC multiplayer testing.
    /// This intentionally uses one game process: Steam P2P cannot reliably
    /// impersonate two different Steam users from the same account.
    /// </summary>
    public sealed class LocalDevMenu : MonoBehaviour
    {
        static readonly Color PanelColour = new Color(0.06f, 0.04f, 0.025f, 0.94f);
        static readonly Color BorderColour = new Color(0.84f, 0.63f, 0.30f, 0.60f);
        static readonly Color TitleColour = new Color(0.98f, 0.86f, 0.36f, 1f);
        static readonly Color BodyColour = new Color(0.94f, 0.90f, 0.76f, 1f);
        static readonly Color MutedColour = new Color(0.72f, 0.68f, 0.58f, 0.95f);
        static readonly Color DisabledColour = new Color(0.40f, 0.36f, 0.30f, 0.90f);
        static readonly Color GoodColour = new Color(0.50f, 0.92f, 0.58f, 1f);
        static readonly Color BadColour = new Color(0.94f, 0.34f, 0.22f, 1f);

        const int StartIndex = 0;
        const int StartAndSaveIndex = 1;
        const int StopIndex = 2;
        const int DiagnosticsIndex = 3;
        const int CloseIndex = 4;

        public static LocalDevMenu Instance { get; private set; }

        CanvasGroup _group;
        Text _title;
        Text _status;
        Text _body;
        Text _twoWindowNote;
        Text _hint;
        Button[] _buttons;
        Text[] _buttonLabels;
        Image[] _buttonBackgrounds;
        int _selection;
        bool _visible;
        string _lastAction = "Press F11, then choose how you want to test.";
        float _lastNavAt = -1f;

        public static void Ensure()
        {
            if (Instance != null)
                return;

            var go = new GameObject("CupHeads_LocalDevMenu");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<LocalDevMenu>();
        }

        public static void Toggle()
        {
            Ensure();
            if (Instance != null)
                Instance.SetVisible(!Instance._visible);
        }

        public static void Hide()
        {
            if (Instance != null)
                Instance.SetVisible(false);
        }

        void Awake()
        {
            BuildUi();
            SetVisible(false);
        }

        void Update()
        {
            if (!_visible)
                return;

            RefreshUi();

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                SetVisible(false);
                return;
            }

            if (Time.unscaledTime - _lastNavAt > 0.12f)
            {
                if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S))
                {
                    _lastNavAt = Time.unscaledTime;
                    MoveSelection(1);
                    return;
                }

                if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W))
                {
                    _lastNavAt = Time.unscaledTime;
                    MoveSelection(-1);
                    return;
                }
            }

            if (Input.GetKeyDown(KeyCode.Return)
             || Input.GetKeyDown(KeyCode.KeypadEnter)
             || Input.GetKeyDown(KeyCode.Space)
             || Input.GetKeyDown(KeyCode.Z))
            {
                ExecuteSelection();
            }
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        void BuildUi()
        {
            EnsureEventSystem();

            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 240;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            gameObject.AddComponent<GraphicRaycaster>();

            var dim = new GameObject("Dim");
            dim.transform.SetParent(transform, false);
            var dimRt = dim.AddComponent<RectTransform>();
            dimRt.anchorMin = Vector2.zero;
            dimRt.anchorMax = Vector2.one;
            dimRt.offsetMin = Vector2.zero;
            dimRt.offsetMax = Vector2.zero;
            dim.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.56f);

            var panel = new GameObject("Panel");
            panel.transform.SetParent(transform, false);
            var panelRt = panel.AddComponent<RectTransform>();
            panelRt.anchorMin = panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.anchoredPosition = Vector2.zero;
            panelRt.sizeDelta = new Vector2(900f, 620f);
            CupheadUiTheme.StyleCard(panel.AddComponent<Image>(), dark: true);

            _group = gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;

            _title = MakeText(panel, "CUPHEADS DEV LAB", 28, TitleColour, new Vector2(0f, 254f), new Vector2(820f, 40f), TextAnchor.MiddleCenter);
            _status = MakeText(panel, string.Empty, 16, GoodColour, new Vector2(0f, 214f), new Vector2(820f, 34f), TextAnchor.MiddleCenter);
            _body = MakeText(panel, string.Empty, 14, BodyColour, new Vector2(0f, 104f), new Vector2(780f, 170f), TextAnchor.UpperLeft);
            _body.horizontalOverflow = HorizontalWrapMode.Wrap;
            _body.verticalOverflow = VerticalWrapMode.Overflow;
            _body.lineSpacing = 1.08f;

            _twoWindowNote = MakeText(panel, string.Empty, 12, MutedColour, new Vector2(0f, -28f), new Vector2(780f, 82f), TextAnchor.UpperLeft);
            _twoWindowNote.horizontalOverflow = HorizontalWrapMode.Wrap;
            _twoWindowNote.verticalOverflow = VerticalWrapMode.Overflow;
            _twoWindowNote.lineSpacing = 1.05f;

            _buttons = new Button[5];
            _buttonLabels = new Text[5];
            _buttonBackgrounds = new Image[5];

            BuildButton(panel, StartIndex, "START SIMULATOR", new Vector2(-224f, -146f));
            BuildButton(panel, StartAndSaveIndex, "START + OPEN SAVE SELECT", new Vector2(224f, -146f));
            BuildButton(panel, StopIndex, "STOP SIMULATOR", new Vector2(-224f, -210f));
            BuildButton(panel, DiagnosticsIndex, "COPY DIAGNOSTICS", new Vector2(224f, -210f));
            BuildButton(panel, CloseIndex, "CLOSE", new Vector2(0f, -274f));

            _hint = MakeText(panel, "F11: open/close  |  Up/Down: select  |  Z/Enter: confirm  |  Esc: close", 11, MutedColour, new Vector2(0f, -310f), new Vector2(820f, 24f), TextAnchor.MiddleCenter);
        }

        void BuildButton(GameObject parent, int index, string label, Vector2 pos)
        {
            var go = new GameObject("Button_" + label);
            go.transform.SetParent(parent.transform, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = index == CloseIndex ? new Vector2(320f, 46f) : new Vector2(400f, 46f);

            var image = go.AddComponent<Image>();
            CupheadUiTheme.StyleCard(image, dark: true);
            image.color = new Color(0.72f, 0.68f, 0.60f, 0.95f);
            _buttonBackgrounds[index] = image;

            var button = go.AddComponent<Button>();
            int captured = index;
            button.onClick.AddListener(() =>
            {
                _selection = captured;
                ExecuteSelection();
            });
            _buttons[index] = button;

            var text = MakeText(go, label, 15, BodyColour, Vector2.zero, rt.sizeDelta, TextAnchor.MiddleCenter);
            _buttonLabels[index] = text;
        }

        void SetVisible(bool visible)
        {
            if (visible)
                EnsureEventSystem();

            _visible = visible;
            if (_group != null)
            {
                _group.alpha = visible ? 1f : 0f;
                _group.interactable = visible;
                _group.blocksRaycasts = visible;
            }

            if (visible)
                RefreshUi();
        }

        void RefreshUi()
        {
            CupheadUiTheme.RefreshLabelFonts(gameObject);

            bool enabled = Plugin.EnableLocalDevSession;
            bool connected = Plugin.Net != null && Plugin.Net.IsConnected;
            bool active = LocalDevSession.IsActive;
            string sceneName = SceneManager.GetActiveScene().name;

            if (_status != null)
            {
                if (!enabled)
                {
                    _status.text = "DISABLED IN CONFIG";
                    _status.color = BadColour;
                }
                else if (connected)
                {
                    _status.text = "REAL STEAM SESSION ACTIVE - DEV SIM LOCKED";
                    _status.color = BadColour;
                }
                else if (active)
                {
                    _status.text = "SIMULATOR ACTIVE - P2 IS DRIVEN THROUGH REMOTE INPUT";
                    _status.color = GoodColour;
                }
                else
                {
                    _status.text = "READY - ONE WINDOW LOCAL MULTIPLAYER SIM";
                    _status.color = TitleColour;
                }
            }

            if (_body != null)
            {
                _body.text =
                    "Scene: " + (string.IsNullOrEmpty(sceneName) ? "unknown" : sceneName) + "\n"
                  + "Last action: " + _lastAction + "\n\n"
                  + "This starts CupHeads as a host-style multiplayer session without Steam. "
                  + "Player One stays native/local. Player Two is marked as the network-controlled participant, "
                  + "then fed through RemoteInputDriver from Player Two/controller bindings.\n\n"
                  + "Use this to test spawn, map movement, shooting, equip/shop menus, deaths, revives, and boss fights without waiting for a second person.";
            }

            if (_twoWindowNote != null)
            {
                _twoWindowNote.text =
                    "Two-window note: I am not adding an automatic second Cuphead.exe launcher yet. "
                  + "Steam P2P uses Steam IDs, so two game windows on one Steam account usually cannot prove real P2P behavior and can cause false bugs. "
                  + "For true two-window testing, use two Windows users/Steam accounts or a second PC.";
            }

            SetButtonState(StartIndex, enabled && !connected && !active);
            SetButtonState(StartAndSaveIndex, enabled && !connected);
            SetButtonState(StopIndex, active);
            SetButtonState(DiagnosticsIndex, true);
            SetButtonState(CloseIndex, true);
            NormalizeSelection();
            RefreshSelection();
        }

        void SetButtonState(int index, bool enabled)
        {
            if (_buttons != null && index >= 0 && index < _buttons.Length && _buttons[index] != null)
                _buttons[index].interactable = enabled;
        }

        void MoveSelection(int delta)
        {
            if (_buttons == null || _buttons.Length == 0)
                return;

            int start = _selection;
            for (int i = 0; i < _buttons.Length; i++)
            {
                _selection = (_selection + delta + _buttons.Length) % _buttons.Length;
                if (_buttons[_selection] != null && _buttons[_selection].interactable)
                    break;
            }

            if (_selection == start)
                NormalizeSelection();
            RefreshSelection();
        }

        void NormalizeSelection()
        {
            if (_buttons == null || _buttons.Length == 0)
                return;

            if (_selection >= 0 && _selection < _buttons.Length && _buttons[_selection] != null && _buttons[_selection].interactable)
                return;

            for (int i = 0; i < _buttons.Length; i++)
            {
                if (_buttons[i] != null && _buttons[i].interactable)
                {
                    _selection = i;
                    return;
                }
            }

            _selection = 0;
        }

        void RefreshSelection()
        {
            if (_buttonLabels == null || _buttonBackgrounds == null || _buttons == null)
                return;

            for (int i = 0; i < _buttonLabels.Length; i++)
            {
                bool enabled = _buttons[i] == null || _buttons[i].interactable;
                bool selected = i == _selection && enabled;
                if (_buttonLabels[i] != null)
                    _buttonLabels[i].color = enabled ? (selected ? TitleColour : BodyColour) : DisabledColour;
                // Tints over the shared card sprite: bright gold when selected,
                // neutral when idle, faded when disabled.
                if (_buttonBackgrounds[i] != null)
                    _buttonBackgrounds[i].color = selected
                        ? new Color(1f, 0.92f, 0.66f, 1f)
                        : enabled
                            ? new Color(0.72f, 0.68f, 0.60f, 0.95f)
                            : new Color(0.42f, 0.40f, 0.36f, 0.70f);
            }
        }

        void ExecuteSelection()
        {
            if (_buttons != null
             && _selection >= 0
             && _selection < _buttons.Length
             && _buttons[_selection] != null
             && !_buttons[_selection].interactable)
                return;

            switch (_selection)
            {
                case StartIndex:
                    StartSimulator(false);
                    break;
                case StartAndSaveIndex:
                    StartSimulator(true);
                    break;
                case StopIndex:
                    LocalDevSession.Stop("Local dev session stopped.");
                    _lastAction = "Stopped the local simulator.";
                    break;
                case DiagnosticsIndex:
                    CopyDiagnostics();
                    break;
                case CloseIndex:
                    SetVisible(false);
                    break;
            }

            RefreshUi();
        }

        void StartSimulator(bool openSaveSelect)
        {
            string message;
            bool ok = openSaveSelect
                ? LocalDevSession.StartAndOpenSaveSelect(out message)
                : LocalDevSession.Start(out message);

            _lastAction = message;
            if (!string.IsNullOrEmpty(message))
                ConnectionHUD.Show(message);

            if (ok && openSaveSelect)
                SetVisible(false);
        }

        void CopyDiagnostics()
        {
            GUIUtility.systemCopyBuffer = Plugin.BuildDiagnosticsReport()
                + "\n\nLocal Dev Menu: " + (LocalDevSession.IsActive ? "active" : "inactive")
                + "\nTwo-window local Steam self-test: unsupported; use two accounts/PCs for true P2P.";
            _lastAction = "Copied diagnostics to clipboard.";
            ConnectionHUD.Show("Diagnostics copied to clipboard.");
        }

        static Text MakeText(GameObject parent, string content, int size, Color color, Vector2 offset, Vector2 sizeDelta, TextAnchor anchor)
        {
            return CupheadUiTheme.MakeLabel(
                parent, content, size, color, offset, sizeDelta,
                anchor, new Vector2(0.5f, 0.5f));
        }

        static void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null)
                return;

            var go = new GameObject("CupHeads_LocalDevEventSystem");
            DontDestroyOnLoad(go);
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
        }
    }
}
