using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using CupheadOnline.Patches;
using CupheadOnline.Sync;

namespace CupheadOnline.UI
{
    /// <summary>
    /// The in-game connection tag: a small printed paper card in the top-right
    /// corner, in the game's own ink and type. Cuphead keeps its screen clear,
    /// so the tag only shows when it has something to say:
    ///   • a message (partner paused, chose Retry, joined…) for a few seconds,
    ///   • your partner's name and ping for a moment as each level starts,
    ///   • while the connection is laggy or a sync problem is being fixed,
    ///   • when the partner disconnects.
    /// </summary>
    public class ConnectionHUD : MonoBehaviour
    {
        public static ConnectionHUD Instance { get; private set; }

        const float MessageSeconds = 3.5f;
        const float LevelIntroSeconds = 4f;
        const float DisconnectSeconds = 8f;
        const float Width = 380f;

        Image _card;
        CanvasGroup _group;
        RectTransform _cardRt;
        Text _title;
        Text _line;
        Image[] _signal;

        float _alpha;
        string _message = string.Empty;
        float _messageUntil;
        bool _disconnected;
        float _disconnectedUntil;
        int _ping;
        float _introUntil;
        string _lastScene = string.Empty;

        // ── API (unchanged) ───────────────────────────────────────────────────

        public static void Show()
        {
            if (!Plugin.ShowConnectionHud)
            {
                Hide();
                return;
            }

            if (Instance != null) return;

            var go = NativeUiKit.MakeOverlayCanvas("CupheadOnline_HUD", 150);
            Instance = go.AddComponent<ConnectionHUD>();
        }

        /// <summary>Shows a short message on the tag.</summary>
        public static void Show(string status)
        {
            if (!Plugin.ShowConnectionHud)
            {
                Hide();
                return;
            }

            Show();
            if (Instance != null)
                Instance.SetMessage(status);
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
                Instance._ping = ms;
        }

        public static void ShowDisconnected(string reason)
        {
            if (!Plugin.ShowConnectionHud)
            {
                Hide();
                return;
            }

            Show();
            if (Instance == null) return;
            Instance._disconnected = true;
            Instance._disconnectedUntil = Time.unscaledTime + DisconnectSeconds;
            Instance._message = string.IsNullOrEmpty(reason) ? "The connection closed." : reason;
            Instance._messageUntil = Instance._disconnectedUntil;
        }

        // ── View ──────────────────────────────────────────────────────────────

        void Awake()
        {
            _card = NativeUiKit.MakeCard(gameObject, "Tag", Vector2.zero, new Vector2(Width, 100f), false, null);
            _cardRt = _card.rectTransform;
            _cardRt.anchorMin = _cardRt.anchorMax = _cardRt.pivot = new Vector2(1f, 1f);
            _cardRt.anchoredPosition = new Vector2(-28f, -24f);

            _group = _card.gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;

            _title = NativeUiKit.MakeText(_card.gameObject, "Title", FontRole.Vogue, 26, NativeUiKit.Ink,
                TextAnchor.UpperLeft, Vector2.zero, new Vector2(Width - 110f, 34f));
            Anchor(_title.rectTransform, new Vector2(28f, -20f));
            _title.horizontalOverflow = HorizontalWrapMode.Overflow;

            _line = NativeUiKit.MakeText(_card.gameObject, "Line", FontRole.Memphis, 19, NativeUiKit.InkSoft,
                TextAnchor.UpperLeft, Vector2.zero, new Vector2(Width - 56f, 60f));
            Anchor(_line.rectTransform, new Vector2(28f, -54f));
            _line.lineSpacing = 0.95f;

            var signalRoot = NativeUiKit.MakeRect(_card.gameObject, "Signal", Vector2.zero, new Vector2(30f, 22f));
            signalRoot.anchorMin = signalRoot.anchorMax = signalRoot.pivot = new Vector2(1f, 1f);
            signalRoot.anchoredPosition = new Vector2(-30f, -24f);
            _signal = NativeUiKit.MakeSignal(signalRoot.gameObject, new Vector2(0f, 0f), 1.1f);
        }

        static void Anchor(RectTransform rt, Vector2 topLeftOffset)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = topLeftOffset;
        }

        void SetMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            _message = message;
            _messageUntil = Time.unscaledTime + MessageSeconds;
        }

        void Update()
        {
            NativeUiKit.ApplyCard(_card, false);
            float now = Time.unscaledTime;
            var net = Plugin.Net;
            bool connected = net != null && net.IsConnected;
            if (connected)
                _disconnected = false;

            // Introduce the partner briefly as each level starts.
            string scene = SceneManager.GetActiveScene().name ?? string.Empty;
            if (scene != _lastScene)
            {
                _lastScene = scene;
                if (connected && MultiplayerSession.IsActive && Level.Current != null)
                    _introUntil = now + LevelIntroSeconds;
            }

            bool preview = UiPreview.Overlays;
            if (preview)
            {
                connected = true;
                _ping = UiPreview.Ping;
            }

            bool laggy = connected && _ping >= 150;
            bool desync = connected && !preview && SessionSync.DesyncSeverity >= SessionIssueSeverity.Warning;
            bool show = preview || (!MpMenuState.InMpMenu
                && (now < _messageUntil
                    || (_disconnected && now < _disconnectedUntil)
                    || laggy || desync
                    || (connected && now < _introUntil)));

            _alpha = Mathf.MoveTowards(_alpha, show ? 1f : 0f, Time.unscaledDeltaTime * (show ? 6f : 2.5f));
            _group.alpha = _alpha;
            if (_alpha <= 0f)
                return;

            // Title: who you are playing with.
            string title;
            Color titleColor = NativeUiKit.Ink;
            if (_disconnected)
            {
                title = "DISCONNECTED";
                titleColor = NativeUiKit.Red;
            }
            else if (preview)
            {
                title = UiPreview.PartnerName;
            }
            else if (connected)
            {
                string name = net.CurrentPeerName;
                title = string.IsNullOrEmpty(name) || name == "Unknown Player" ? "YOUR PARTNER" : name.ToUpperInvariant();
                if (title.Length > 18) title = title.Substring(0, 17) + "…";
            }
            else
            {
                title = "CUPHEADS";
            }
            if (_title.text != title) _title.text = title;
            _title.color = titleColor;

            // Line: the newest message, else the connection itself.
            string line;
            Color lineColor = NativeUiKit.InkSoft;
            if (now < _messageUntil || _disconnected)
            {
                line = _message;
                if (_disconnected) lineColor = NativeUiKit.Ink;
            }
            else if (desync)
            {
                line = SessionSync.DesyncSummary;
                lineColor = NativeUiKit.WarnInk;
            }
            else if (connected)
            {
                string character = preview ? UiPreview.PartnerCharacter
                    : MultiplayerSession.IsActive ? MultiplayerSession.GetRemoteCharacterName() : string.Empty;
                string ping = _ping > 0 ? _ping + " ms, " + NativeUiKit.PingWord(_ping) : "checking ping";
                line = string.IsNullOrEmpty(character) ? ping : character + "  ·  " + ping;
                if (!preview && ExtraParticipantTracker.TotalCount > 0)
                    line += "  ·  +" + ExtraParticipantTracker.TotalCount + " more";
                if (laggy) lineColor = NativeUiKit.Red;
            }
            else
            {
                line = _message;
            }
            if (_line.text != line) _line.text = line;
            _line.color = lineColor;

            NativeUiKit.SetSignal(_signal, _ping, connected);
            _signal[0].transform.parent.gameObject.SetActive(connected || _disconnected);

            // Grow the card for two- or three-line messages.
            float height = Mathf.Clamp(64f + _line.preferredHeight, 96f, 150f);
            if (Mathf.Abs(_cardRt.sizeDelta.y - height) > 0.5f)
                _cardRt.sizeDelta = new Vector2(Width, height);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
