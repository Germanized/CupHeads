using UnityEngine;
using UnityEngine.UI;
using CupheadOnline.Net;
using CupheadOnline.Sync;

namespace CupheadOnline.UI
{
    /// <summary>
    /// Shared results card on the knockout screen: both players' deaths, parries,
    /// and retries side by side, printed on the game's paper card beside its own
    /// results board. Each peer sends a StatsReportPacket when the win scene
    /// loads; the card fills in the partner column when it arrives.
    /// </summary>
    public static class WinStatsCard
    {
        static GameObject _root;
        static CanvasGroup _group;
        static Image _card;
        static Text _localName;
        static Text _localStats;
        static Text _remoteName;
        static Text _remoteStats;
        static bool _visible;

        static StatsReportPacket? _remoteReport;

        public static void OnSceneLoaded(string sceneName)
        {
            bool isWinScene = string.Equals(sceneName, "scene_win", System.StringComparison.OrdinalIgnoreCase);

            if (!isWinScene)
            {
                Hide();
                _remoteReport = null;
                return;
            }

            if (!Plugin.EnableWinStatsCard || !MultiplayerSession.IsActive
             || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            // Tell the partner our numbers, then show the card.
            var pkt = new StatsReportPacket
            {
                ParticipantId = (byte)MultiplayerSession.LocalId,
                Deaths = ClampStat(SessionSync.LocalDeaths),
                Parries = ClampStat(SessionSync.LocalParries),
                Retries = ClampStat(SessionSync.LocalRetries),
            };
            Plugin.Net.SendStatsReport(ref pkt);
            Show();
        }

        public static void OnRemoteStats(StatsReportPacket pkt)
        {
            _remoteReport = pkt;
            if (_visible)
                RefreshColumns();
        }

        public static void Tick()
        {
            if (!_visible || _root == null)
                return;

            NativeUiKit.ApplyCard(_card, false);
            RefreshColumns();
        }

        public static void Hide()
        {
            _visible = false;
            if (_group != null)
                _group.alpha = 0f;
        }

        /// <summary>UiPreview: the card with sample partner numbers.</summary>
        public static void ShowPreview()
        {
            _remoteReport = new StatsReportPacket { ParticipantId = 1, Deaths = 2, Parries = 5, Retries = 1 };
            Show();
        }

        static void Show()
        {
            EnsureUi();
            _visible = true;
            if (_group != null)
                _group.alpha = 1f;
            RefreshColumns();
        }

        static string Lines(int deaths, int parries, int retries)
        {
            return "Deaths  " + deaths + "\nParries  " + parries + "\nRetries  " + retries;
        }

        static void RefreshColumns()
        {
            if (_localName != null)
            {
                _localName.text = MultiplayerSession.GetLocalCharacterName().ToUpperInvariant();
                _localStats.text = Lines(SessionSync.LocalDeaths, SessionSync.LocalParries, SessionSync.LocalRetries);
            }

            if (_remoteName != null)
            {
                _remoteName.text = MultiplayerSession.GetRemoteCharacterName().ToUpperInvariant();
                if (_remoteReport.HasValue)
                {
                    var stats = _remoteReport.Value;
                    _remoteStats.text = Lines(stats.Deaths, stats.Parries, stats.Retries);
                }
                else
                {
                    int n = Mathf.FloorToInt(Time.unscaledTime / 0.4f) % 4;
                    _remoteStats.text = "waiting" + new string('.', n);
                }
            }
        }

        static void EnsureUi()
        {
            if (_root != null)
                return;

            _root = NativeUiKit.MakeOverlayCanvas("CupHeads_WinStatsCard", 155);

            _card = NativeUiKit.MakeCard(_root, "Card", Vector2.zero, new Vector2(430f, 290f), false, "Results");
            var rt = _card.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.anchoredPosition = new Vector2(-40f, 0f);

            _group = _card.gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;

            var title = NativeUiKit.MakeText(_card.gameObject, "Title", FontRole.Vogue, 30, NativeUiKit.Ink,
                TextAnchor.MiddleCenter, Vector2.zero, new Vector2(390f, 38f));
            NativeUiKit.TopAnchor(title.rectTransform, 0f, 26f);
            title.text = "TEAM RESULTS";

            _localName = Column(-98f, 76f, FontRole.Vogue, 22, NativeUiKit.Red);
            _localStats = Column(-98f, 108f, FontRole.Memphis, 20, NativeUiKit.Ink);
            _remoteName = Column(98f, 76f, FontRole.Vogue, 22, NativeUiKit.Red);
            _remoteStats = Column(98f, 108f, FontRole.Memphis, 20, NativeUiKit.Ink);
        }

        static Text Column(float x, float yFromTop, FontRole font, int size, Color color)
        {
            var text = NativeUiKit.MakeText(_card.gameObject, "Column", font, size, color,
                TextAnchor.UpperCenter, Vector2.zero, new Vector2(180f, size > 21 ? 30f : 96f));
            NativeUiKit.TopAnchor(text.rectTransform, x, yFromTop);
            text.lineSpacing = 1.15f;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            return text;
        }

        static ushort ClampStat(int value)
        {
            return (ushort)Mathf.Clamp(value, 0, ushort.MaxValue);
        }
    }
}
