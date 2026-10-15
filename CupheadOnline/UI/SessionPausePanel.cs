using UnityEngine;
using UnityEngine.UI;
using CupheadOnline.Sync;

namespace CupheadOnline.UI
{
    /// <summary>
    /// While the game is paused in a session, a small printed card beside the
    /// pause menu says who you are playing with and how the connection is —
    /// and, only when there is one, what is wrong. F8 keeps it up unpaused.
    /// (The full technical readout lives in the bug report.)
    /// </summary>
    public sealed class SessionPausePanel : MonoBehaviour
    {
        public static SessionPausePanel Instance { get; private set; }

        const float Width = 400f;

        CanvasGroup _group;
        Image _card;
        RectTransform _cardRt;
        Text _name;
        Text _character;
        Text _connection;
        Text _issue;
        Image[] _signal;
        bool _manualVisible;
        float _alpha;

        public static void Ensure()
        {
            if (Instance != null) return;

            var go = NativeUiKit.MakeOverlayCanvas("CupheadOnline_SessionPanel", 160);
            Instance = go.AddComponent<SessionPausePanel>();
        }

        void Awake()
        {
            _card = NativeUiKit.MakeCard(gameObject, "Card", Vector2.zero, new Vector2(Width, 236f), false, "Multiplayer");
            _cardRt = _card.rectTransform;
            _cardRt.anchorMin = _cardRt.anchorMax = new Vector2(1f, 0.5f);
            _cardRt.pivot = new Vector2(1f, 0.5f);
            _cardRt.anchoredPosition = new Vector2(-48f, 0f);

            _group = _card.gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;

            var heading = NativeUiKit.MakeText(_card.gameObject, "Heading", FontRole.Memphis, 16, NativeUiKit.InkSoft,
                TextAnchor.MiddleCenter, Vector2.zero, new Vector2(Width - 60f, 22f));
            NativeUiKit.TopAnchor(heading.rectTransform, 0f, 30f);
            heading.text = "P L A Y I N G   W I T H";

            _name = NativeUiKit.MakeText(_card.gameObject, "Name", FontRole.Vogue, 34, NativeUiKit.Ink,
                TextAnchor.MiddleCenter, Vector2.zero, new Vector2(Width - 50f, 42f));
            NativeUiKit.TopAnchor(_name.rectTransform, 0f, 52f);
            _name.horizontalOverflow = HorizontalWrapMode.Overflow;

            _character = NativeUiKit.MakeText(_card.gameObject, "Character", FontRole.Memphis, 20, NativeUiKit.Ink,
                TextAnchor.MiddleCenter, Vector2.zero, new Vector2(Width - 60f, 26f));
            NativeUiKit.TopAnchor(_character.rectTransform, 0f, 94f);

            var signalRoot = NativeUiKit.MakeRect(_card.gameObject, "Signal", Vector2.zero, new Vector2(30f, 22f));
            NativeUiKit.TopAnchor(signalRoot, -96f, 132f);
            _signal = NativeUiKit.MakeSignal(signalRoot.gameObject, Vector2.zero, 1.1f);

            _connection = NativeUiKit.MakeText(_card.gameObject, "Connection", FontRole.Memphis, 20, NativeUiKit.Ink,
                TextAnchor.MiddleLeft, Vector2.zero, new Vector2(220f, 26f));
            NativeUiKit.TopAnchor(_connection.rectTransform, 34f, 130f);
            _connection.horizontalOverflow = HorizontalWrapMode.Overflow;

            _issue = NativeUiKit.MakeText(_card.gameObject, "Issue", FontRole.Memphis, 17, NativeUiKit.WarnInk,
                TextAnchor.UpperCenter, Vector2.zero, new Vector2(Width - 70f, 60f));
            NativeUiKit.TopAnchor(_issue.rectTransform, 0f, 166f);
            _issue.lineSpacing = 0.95f;
        }

        void Update()
        {
            NativeUiKit.ApplyCard(_card, false);

            if (Input.GetKeyDown(KeyCode.F8))
                _manualVisible = !_manualVisible;

            bool preview = UiPreview.Overlays;
            bool shouldShow = preview || (Plugin.ShowPauseSessionPanel
                           && MultiplayerSession.IsActive
                           && (_manualVisible || PauseManager.state == PauseManager.State.Paused));

            _alpha = Mathf.MoveTowards(_alpha, shouldShow ? 1f : 0f, Time.unscaledDeltaTime * 8f);
            _group.alpha = _alpha;
            if (_alpha <= 0f) return;

            var net = Plugin.Net;
            bool connected = preview || (net != null && net.IsConnected);

            string name = preview ? UiPreview.PartnerName : connected ? net.CurrentPeerName : null;
            if (string.IsNullOrEmpty(name) || name == "Unknown Player")
                name = connected ? "Your partner" : "Nobody yet";
            if (name.Length > 18) name = name.Substring(0, 17) + "…";
            Set(_name, name.ToUpperInvariant(), NativeUiKit.Ink);

            string character = preview ? UiPreview.PartnerCharacter : MultiplayerSession.GetRemoteCharacterName();
            Set(_character, string.IsNullOrEmpty(character) ? string.Empty : "as " + character, NativeUiKit.InkSoft);

            int ping = preview ? UiPreview.Ping : connected ? net.Latency : 0;
            string connection = !connected ? "not connected"
                : ping > 0 ? ping + " ms, " + NativeUiKit.PingWord(ping)
                : "checking ping";
            Set(_connection, connection, !connected || ping >= 150 ? NativeUiKit.Red : NativeUiKit.Ink);
            NativeUiKit.SetSignal(_signal, ping, connected);

            string issue = string.Empty;
            if (preview)
                issue = string.Empty;
            else if (SessionSync.DesyncSeverity >= SessionIssueSeverity.Warning)
                issue = SessionSync.DesyncSummary;
            else if (SessionSync.CompatibilitySeverity >= SessionIssueSeverity.Warning)
                issue = SessionSync.CompatibilitySummary;
            else if (net != null && net.IsHost && ExtraParticipantTracker.TotalCount > 0)
                issue = "+" + ExtraParticipantTracker.TotalCount + " more player(s) in this session";
            Set(_issue, issue, NativeUiKit.WarnInk);

            // Content, then room for the printed small print at the bottom.
            float height = string.IsNullOrEmpty(issue) ? 222f : 166f + _issue.preferredHeight + 72f;
            if (Mathf.Abs(_cardRt.sizeDelta.y - height) > 0.5f)
                _cardRt.sizeDelta = new Vector2(Width, height);
        }

        static void Set(Text text, string value, Color color)
        {
            if (text.text != value) text.text = value;
            text.color = color;
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }
    }
}
