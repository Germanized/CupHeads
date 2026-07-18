using UnityEngine;
using UnityEngine.UI;
using CupheadOnline.Net;
using CupheadOnline.Sync;

namespace CupheadOnline.UI
{
    /// <summary>
    /// Shared results card on the knockout screen: both players' deaths, parries,
    /// and retries side by side. Each peer sends a StatsReportPacket when the win
    /// scene loads; the card fills in the partner column when it arrives.
    /// </summary>
    public static class WinStatsCard
    {
        static GameObject _root;
        static CanvasGroup _group;
        static Text _localColumn;
        static Text _remoteColumn;
        static bool _visible;

        static StatsReportPacket? _remoteStats;

        public static void OnSceneLoaded(string sceneName)
        {
            bool isWinScene = string.Equals(sceneName, "scene_win", System.StringComparison.OrdinalIgnoreCase);

            if (!isWinScene)
            {
                Hide();
                _remoteStats = null;
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
            _remoteStats = pkt;
            if (_visible)
                RefreshColumns();
        }

        public static void Tick()
        {
            if (!_visible || _root == null)
                return;

            CupheadUiTheme.RefreshLabelFonts(_root);
            RefreshColumns();
        }

        public static void Hide()
        {
            _visible = false;
            if (_group != null)
                _group.alpha = 0f;
        }

        static void Show()
        {
            EnsureUi();
            _visible = true;
            if (_group != null)
                _group.alpha = 1f;
            RefreshColumns();
        }

        static void RefreshColumns()
        {
            if (_localColumn != null)
            {
                _localColumn.text = MultiplayerSession.GetLocalCharacterName().ToUpperInvariant()
                    + "\nDEATHS " + SessionSync.LocalDeaths
                    + "\nPARRIES " + SessionSync.LocalParries
                    + "\nRETRIES " + SessionSync.LocalRetries;
            }

            if (_remoteColumn != null)
            {
                if (_remoteStats.HasValue)
                {
                    var stats = _remoteStats.Value;
                    _remoteColumn.text = MultiplayerSession.GetRemoteCharacterName().ToUpperInvariant()
                        + "\nDEATHS " + stats.Deaths
                        + "\nPARRIES " + stats.Parries
                        + "\nRETRIES " + stats.Retries;
                }
                else
                {
                    _remoteColumn.text = MultiplayerSession.GetRemoteCharacterName().ToUpperInvariant()
                        + "\nwaiting...";
                }
            }
        }

        static void EnsureUi()
        {
            if (_root != null)
                return;

            _root = new GameObject("CupHeads_WinStatsCard");
            Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 155;

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var card = new GameObject("Card");
            card.transform.SetParent(_root.transform, false);
            var rt = card.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.anchoredPosition = new Vector2(-30f, 0f);
            rt.sizeDelta = new Vector2(340f, 220f);
            CupheadUiTheme.StyleCard(card.AddComponent<Image>(), dark: true);

            _group = card.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;

            CupheadUiTheme.MakeLabel(
                card, "~ TEAM RESULTS ~", 18, CupheadUiTheme.Gold,
                new Vector2(0f, 82f), new Vector2(310f, 26f),
                TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f));

            _localColumn = CupheadUiTheme.MakeLabel(
                card, string.Empty, 14, CupheadUiTheme.Cream,
                new Vector2(-78f, -18f), new Vector2(146f, 140f),
                TextAnchor.UpperCenter, new Vector2(0.5f, 0.5f));
            _localColumn.lineSpacing = 1.25f;

            _remoteColumn = CupheadUiTheme.MakeLabel(
                card, string.Empty, 14, CupheadUiTheme.Cream,
                new Vector2(78f, -18f), new Vector2(146f, 140f),
                TextAnchor.UpperCenter, new Vector2(0.5f, 0.5f));
            _remoteColumn.lineSpacing = 1.25f;
        }

        static ushort ClampStat(int value)
        {
            return (ushort)Mathf.Clamp(value, 0, ushort.MaxValue);
        }
    }
}
