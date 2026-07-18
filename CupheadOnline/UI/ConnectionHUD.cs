using UnityEngine;
using UnityEngine.UI;
using CupheadOnline.Sync;

namespace CupheadOnline.UI
{
    /// <summary>
    /// Lightweight in-game overlay: status, ping quality, and a little session context.
    /// </summary>
    public class ConnectionHUD : MonoBehaviour
    {
        public static ConnectionHUD Instance { get; private set; }

        private static readonly Color GoodColour         = new Color(0.55f, 0.92f, 0.62f, 1f);
        private static readonly Color OkayColour         = new Color(0.95f, 0.85f, 0.40f, 1f);
        private static readonly Color PoorColour         = new Color(1.00f, 0.55f, 0.10f, 1f);
        private static readonly Color DisconnectedColour = new Color(0.90f, 0.20f, 0.15f, 1f);
        private static readonly Color TextColour         = new Color(0.96f, 0.91f, 0.77f, 1f);
        private static readonly Color MetaColour         = new Color(0.82f, 0.78f, 0.66f, 0.95f);
        // Card tint per connection quality — the card sprite carries the real
        // colours, so these stay close to white and only warm/redden the panel.
        private static readonly Color BgGoodColour       = Color.white;
        private static readonly Color BgOkayColour       = new Color(1f, 0.93f, 0.80f, 1f);
        private static readonly Color BgPoorColour       = new Color(1f, 0.76f, 0.66f, 1f);

        private Text _titleLabel;
        private Text _pingLabel;
        private Text _statusLabel;
        private Text _metaLabel;
        private Text _extrasLabel;
        private Image _bgImage;

        private float _connectedAt = -1f;
        private bool _trackingConnectedSession;
        private bool _showingDisconnectedState;

        public static void Show()
        {
            if (!Plugin.ShowConnectionHud)
            {
                Hide();
                return;
            }

            if (Instance != null) return;

            var go = new GameObject("CupheadOnline_HUD");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<ConnectionHUD>();
        }

        public static void Show(string status)
        {
            if (!Plugin.ShowConnectionHud)
            {
                Hide();
                return;
            }

            Show();
            if (Instance != null)
                Instance.SetConnectedStatus(status);
        }

        public static void Hide()
        {
            if (Instance == null) return;
            Destroy(Instance.gameObject);
            Instance = null;
        }

        public static void UpdatePing(int ms)
        {
            if (!Plugin.ShowConnectionHud)
            {
                Hide();
                return;
            }

            Show();
            if (Instance != null)
                Instance.ApplyPing(ms);
        }

        public static void ShowDisconnected(string reason)
        {
            if (!Plugin.ShowConnectionHud)
            {
                Hide();
                return;
            }

            Show();
            if (Instance != null)
                Instance.ApplyDisconnected(reason);
        }

        void Awake()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 150;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            gameObject.AddComponent<GraphicRaycaster>();

            var bg = new GameObject("HUD_BG");
            bg.transform.SetParent(gameObject.transform, false);
            var bgRT = bg.AddComponent<RectTransform>();
            bgRT.anchorMin = bgRT.anchorMax = new Vector2(1f, 1f);
            bgRT.pivot = new Vector2(1f, 1f);
            bgRT.anchoredPosition = new Vector2(-12f, -12f);
            bgRT.sizeDelta = new Vector2(292f, 128f);
            _bgImage = bg.AddComponent<Image>();
            CupheadUiTheme.StyleCard(_bgImage, dark: true);

            _titleLabel = MakeLabel(bg, "CUPHEADS", 15, CupheadUiTheme.Gold, new Vector2(0f, 40f), new Vector2(270f, 20f));
            _pingLabel = MakeLabel(bg, "PING ---", 13, OkayColour, new Vector2(0f, 20f), new Vector2(270f, 20f));

            _statusLabel = MakeLabel(bg, "Waiting for peer...", 10, TextColour, new Vector2(0f, -4f), new Vector2(270f, 24f));
            _statusLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
            _statusLabel.verticalOverflow = VerticalWrapMode.Overflow;

            _metaLabel = MakeLabel(bg, "", 9, MetaColour, new Vector2(0f, -28f), new Vector2(270f, 18f));
            _extrasLabel = MakeLabel(bg, "", 9, MetaColour, new Vector2(0f, -52f), new Vector2(270f, 30f));
            _extrasLabel.alignment = TextAnchor.UpperCenter;
            _extrasLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
            _extrasLabel.verticalOverflow = VerticalWrapMode.Overflow;
        }

        void Update()
        {
            CupheadUiTheme.RefreshLabelFonts(gameObject);
            RefreshMeta();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void SetConnectedStatus(string status)
        {
            if (!_trackingConnectedSession)
                _connectedAt = Time.unscaledTime;

            _trackingConnectedSession = true;
            _showingDisconnectedState = false;

            if (_titleLabel != null)
            {
                _titleLabel.text = "CUPHEADS";
                _titleLabel.color = CupheadUiTheme.Gold;
            }

            if (_pingLabel != null && string.IsNullOrEmpty(_pingLabel.text))
                _pingLabel.text = "PING ---";

            if (_statusLabel != null)
            {
                _statusLabel.text = string.IsNullOrEmpty(status) ? "Steam P2P connected." : status;
                _statusLabel.color = TextColour;
            }

            if (_bgImage != null)
                _bgImage.color = BgGoodColour;

            RefreshMeta();
        }

        void ApplyPing(int ms)
        {
            string quality;
            Color accent;
            Color bg;

            if (ms < 80)
            {
                quality = "GOOD";
                accent = GoodColour;
                bg = BgGoodColour;
            }
            else if (ms < 150)
            {
                quality = "OKAY";
                accent = OkayColour;
                bg = BgOkayColour;
            }
            else
            {
                quality = "POOR";
                accent = PoorColour;
                bg = BgPoorColour;
            }

            if (_titleLabel != null)
                _titleLabel.color = accent;

            if (_pingLabel != null)
            {
                _pingLabel.text = "PING " + ms + "ms - " + quality;
                _pingLabel.color = accent;
            }

            if (_statusLabel != null && string.IsNullOrEmpty(_statusLabel.text))
                _statusLabel.text = "Steam P2P connected.";

            if (_bgImage != null)
                _bgImage.color = bg;

            RefreshMeta();
        }

        void ApplyDisconnected(string reason)
        {
            _trackingConnectedSession = false;
            _showingDisconnectedState = true;
            _connectedAt = -1f;

            if (_titleLabel != null)
            {
                _titleLabel.text = "CUPHEADS";
                _titleLabel.color = DisconnectedColour;
            }

            if (_pingLabel != null)
            {
                _pingLabel.text = "DISCONNECTED";
                _pingLabel.color = DisconnectedColour;
            }

            if (_statusLabel != null)
            {
                _statusLabel.text = string.IsNullOrEmpty(reason) ? "Connection closed." : reason;
                _statusLabel.color = new Color(1f, 0.78f, 0.72f, 1f);
            }

            if (_bgImage != null)
                _bgImage.color = new Color(1f, 0.58f, 0.50f, 1f);

            RefreshMeta();
        }

        void RefreshMeta()
        {
            if (_metaLabel == null) return;

            if (_showingDisconnectedState)
            {
                string retryHint = "Open Multiplayer to retry.";
                if (_metaLabel.text != retryHint)
                    _metaLabel.text = retryHint;
                _metaLabel.color = new Color(1f, 0.74f, 0.66f, 0.92f);
                if (_extrasLabel != null && !string.IsNullOrEmpty(_extrasLabel.text))
                    _extrasLabel.text = string.Empty;
                return;
            }

            if (Plugin.Net == null)
            {
                if (!string.IsNullOrEmpty(_metaLabel.text))
                    _metaLabel.text = string.Empty;
                if (_extrasLabel != null && !string.IsNullOrEmpty(_extrasLabel.text))
                    _extrasLabel.text = string.Empty;
                return;
            }

            if (Plugin.Net.IsConnected)
            {
                if (_connectedAt < 0f)
                    _connectedAt = Time.unscaledTime;

                if (_statusLabel != null)
                {
                    if (SessionSync.DesyncSeverity >= SessionIssueSeverity.Warning)
                    {
                        _statusLabel.text = SessionSync.DesyncSummary;
                        _statusLabel.color = SessionSync.GetSeverityColor(SessionSync.DesyncSeverity);
                    }
                    else if (SessionSync.CompatibilitySeverity >= SessionIssueSeverity.Warning)
                    {
                        _statusLabel.text = SessionSync.CompatibilitySummary;
                        _statusLabel.color = SessionSync.GetSeverityColor(SessionSync.CompatibilitySeverity);
                    }
                    else
                    {
                        _statusLabel.text = SessionSync.GetStageSummary();
                        _statusLabel.color = TextColour;
                    }
                }

                string line = (Plugin.Net.IsHost ? "HOST" : "CLIENT")
                    + " "
                    + MultiplayerSession.GetLocalCharacterName()
                    + " | "
                    + ShortenPeerName(Plugin.Net.CurrentPeerName);

                string lobbyId = ShortLobbyId(Plugin.Net.CurrentLobbyId);
                if (!string.IsNullOrEmpty(lobbyId))
                    line += " | #" + lobbyId;

                if (Plugin.BossHpScalingEnabled && BossHealthScaler.CurrentMultiplier > 1.0001f)
                    line += " | HP x" + BossHealthScaler.CurrentMultiplier.ToString("0.00");

                if (Plugin.Net.IsHost && Plugin.Net.PendingPeerCount > 0)
                    line += " | +" + Plugin.Net.PendingPeerCount + " queued";

                if (ExtraParticipantTracker.TotalCount > 0)
                    line += " | +" + ExtraParticipantTracker.TotalCount + " extra";

                line += " | " + FormatElapsed(Time.unscaledTime - _connectedAt);

                if (_metaLabel.text != line)
                    _metaLabel.text = line;
                _metaLabel.color = MetaColour;

                if (_extrasLabel != null)
                {
                    string extras = ExtraParticipantTracker.BuildStatusSummary();
                    if (_extrasLabel.text != extras)
                        _extrasLabel.text = extras;
                    _extrasLabel.color = ExtraParticipantTracker.DeadCount > 0
                        ? new Color(1f, 0.74f, 0.66f, 0.92f)
                        : MetaColour;
                }
                return;
            }

            if (Plugin.Net.IsInLobby)
            {
                string lobbyLine = Plugin.Net.IsHost ? "HOST LOBBY" : "IN LOBBY";
                string lobbyId = ShortLobbyId(Plugin.Net.CurrentLobbyId);
                if (!string.IsNullOrEmpty(lobbyId))
                    lobbyLine += " | #" + lobbyId;

                string peerSummary = Plugin.Net.CurrentPeerSummary;
                if (!string.IsNullOrEmpty(peerSummary))
                    lobbyLine += " | " + peerSummary;

                if (_metaLabel.text != lobbyLine)
                    _metaLabel.text = lobbyLine;
                _metaLabel.color = MetaColour;
                if (_extrasLabel != null && !string.IsNullOrEmpty(_extrasLabel.text))
                    _extrasLabel.text = string.Empty;
                return;
            }

            if (!string.IsNullOrEmpty(_metaLabel.text))
                _metaLabel.text = string.Empty;
            if (_extrasLabel != null && !string.IsNullOrEmpty(_extrasLabel.text))
                _extrasLabel.text = string.Empty;
        }

        static string ShortenPeerName(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "Unknown Player")
                return "Peer";

            return name.Length <= 14 ? name : name.Substring(0, 13) + "...";
        }

        static string ShortLobbyId(string lobbyId)
        {
            if (string.IsNullOrEmpty(lobbyId))
                return string.Empty;

            return lobbyId.Length <= 8 ? lobbyId : lobbyId.Substring(lobbyId.Length - 8);
        }

        static string FormatElapsed(float seconds)
        {
            int totalSeconds = Mathf.Max(0, Mathf.FloorToInt(seconds));
            int minutes = totalSeconds / 60;
            int secs = totalSeconds % 60;
            return minutes.ToString("00") + ":" + secs.ToString("00");
        }

        static Text MakeLabel(GameObject parent, string text, int size, Color colour, Vector2 offset, Vector2 sizeDelta)
        {
            return CupheadUiTheme.MakeLabel(
                parent, text, size, colour, offset, sizeDelta,
                TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f));
        }
    }
}
