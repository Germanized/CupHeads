using System;
using UnityEngine;
using UnityEngine.UI;
using CupheadOnline.Sync;

namespace CupheadOnline.UI
{
    /// <summary>
    /// Compact battle QoL readout: timer, deaths, retries, parries, and quick
    /// hotkey hints. It deliberately avoids gameplay changes.
    /// </summary>
    public sealed class BattleAssistHud : MonoBehaviour
    {
        public static BattleAssistHud Instance { get; private set; }

        private static readonly Color TitleColour = new Color(0.95f, 0.86f, 0.42f, 1f);
        private static readonly Color BodyColour = new Color(0.95f, 0.91f, 0.76f, 0.96f);
        private static readonly Color HintColour = new Color(0.72f, 0.70f, 0.62f, 0.92f);
        private static readonly Color BgColour = new Color(0.05f, 0.03f, 0.02f, 0.74f);

        private static string _trackedLevel = string.Empty;
        private static float _lastTimerTickAt = -1f;
        private static float _elapsedSeconds;
        private static int _deaths;
        private static int _retries;
        private static int _parries;

        private CanvasGroup _canvasGroup;
        private RectTransform _panelRect;
        private Text _title;
        private Text _body;
        private Text _hint;

        public static float ElapsedSeconds => Mathf.Max(0f, _elapsedSeconds);

        internal static bool TryGetPanelScreenRect(out Rect rect)
        {
            rect = new Rect();
            if (Instance == null
             || Instance._panelRect == null
             || Instance._canvasGroup == null
             || Instance._canvasGroup.alpha <= 0.01f)
            {
                return false;
            }

            var corners = new Vector3[4];
            Instance._panelRect.GetWorldCorners(corners);
            float minX = corners[0].x;
            float minY = corners[0].y;
            float maxX = corners[0].x;
            float maxY = corners[0].y;
            for (int i = 1; i < corners.Length; i++)
            {
                minX = Mathf.Min(minX, corners[i].x);
                minY = Mathf.Min(minY, corners[i].y);
                maxX = Mathf.Max(maxX, corners[i].x);
                maxY = Mathf.Max(maxY, corners[i].y);
            }

            rect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return rect.width > 1f && rect.height > 1f;
        }

        public static void Tick()
        {
            if (!Plugin.ShowBattleAssistHud || !IsBattleActive())
            {
                Hide();
                return;
            }

            Ensure();
            TrackCurrentBattle();
            UpdateTimer();
            if (Instance != null)
                Instance.Refresh();
        }

        public static void RecordLocalDeath(LevelPlayerController player)
        {
            if (!ShouldCountPlayer(player))
                return;

            TrackCurrentBattle();
            _deaths++;
        }

        public static void RecordRetry()
        {
            if (!IsBattleActive())
                return;

            _retries++;
            _elapsedSeconds = 0f;
            _lastTimerTickAt = Time.unscaledTime;
        }

        public static void RecordLocalParry(LevelPlayerController player)
        {
            if (!ShouldCountPlayer(player))
                return;

            TrackCurrentBattle();
            _parries++;
        }

        public static void Reset()
        {
            _trackedLevel = string.Empty;
            _lastTimerTickAt = -1f;
            _elapsedSeconds = 0f;
            _deaths = 0;
            _retries = 0;
            _parries = 0;
            Hide();
        }

        public static void Hide()
        {
            if (Instance == null)
                return;

            Destroy(Instance.gameObject);
            Instance = null;
        }

        private static void Ensure()
        {
            if (Instance != null)
                return;

            var go = new GameObject("CupHeads_BattleAssistHud");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<BattleAssistHud>();
        }

        private void Awake()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 142;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            gameObject.AddComponent<GraphicRaycaster>();

            var panel = new GameObject("BattleAssistPanel");
            panel.transform.SetParent(transform, false);
            var rt = panel.AddComponent<RectTransform>();
            _panelRect = rt;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(18f, -18f);
            rt.sizeDelta = new Vector2(330f, 92f);

            var bg = panel.AddComponent<Image>();
            bg.color = BgColour;
            var outline = panel.AddComponent<Outline>();
            outline.effectColor = new Color(0.82f, 0.62f, 0.30f, 0.52f);
            outline.effectDistance = new Vector2(1f, -1f);

            _canvasGroup = panel.AddComponent<CanvasGroup>();
            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.alpha = 1f;

            _title = MakeText(panel, "BATTLE ASSIST", 13, TitleColour, new Vector2(14f, -12f), new Vector2(300f, 20f), TextAnchor.MiddleLeft);
            _body = MakeText(panel, string.Empty, 12, BodyColour, new Vector2(14f, -42f), new Vector2(300f, 32f), TextAnchor.UpperLeft);
            _body.horizontalOverflow = HorizontalWrapMode.Wrap;
            _hint = MakeText(panel, "F6 RESYNC  F7 BARS  F9 COPY DIAG  F10 HIDE", 9, HintColour, new Vector2(14f, -76f), new Vector2(302f, 18f), TextAnchor.MiddleLeft);
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private void Refresh()
        {
            if (_canvasGroup != null)
                _canvasGroup.alpha = Plugin.ShowBattleAssistHud ? 1f : 0f;

            string title = "BATTLE ASSIST";
            if (Level.Current != null)
                title = CleanLevelName(Level.Current.CurrentLevel.ToString());
            if (_title != null && _title.text != title)
                _title.text = title;

            string body = "TIME " + FormatTime(ElapsedSeconds)
                + "   DEATHS " + _deaths
                + Environment.NewLine
                + "RETRIES " + _retries
                + "   PARRIES " + _parries;

            if (Plugin.BossHpScalingEnabled && BossHealthScaler.CurrentMultiplier > 1.0001f)
                body += "   HP x" + BossHealthScaler.CurrentMultiplier.ToString("0.00");

            if (_body != null && _body.text != body)
                _body.text = body;

            if (_hint != null)
                _hint.gameObject.SetActive(Plugin.EnableQoLHotkeys);
        }

        private static void TrackCurrentBattle()
        {
            if (!IsBattleActive())
                return;

            string levelKey = Level.Current.CurrentLevel.ToString();
            if (string.Equals(_trackedLevel, levelKey, StringComparison.OrdinalIgnoreCase))
                return;

            _trackedLevel = levelKey;
            _lastTimerTickAt = Time.unscaledTime;
            _elapsedSeconds = 0f;
            _deaths = 0;
            _retries = 0;
            _parries = 0;
        }

        private static void UpdateTimer()
        {
            float now = Time.unscaledTime;
            if (_lastTimerTickAt < 0f)
            {
                _lastTimerTickAt = now;
                return;
            }

            bool paused;
            try { paused = PauseManager.state == PauseManager.State.Paused || CupheadTime.IsPaused(); }
            catch { paused = PauseManager.state == PauseManager.State.Paused; }

            if (!paused)
                _elapsedSeconds += Mathf.Max(0f, now - _lastTimerTickAt);

            _lastTimerTickAt = now;
        }

        private static bool ShouldCountPlayer(LevelPlayerController player)
        {
            if (player == null)
                return false;

            if (MultiplayerSession.IsActive)
                return player.id == MultiplayerSession.LocalId;

            return player.id == PlayerId.PlayerOne;
        }

        private static bool IsBattleActive()
        {
            try
            {
                return Level.Current != null && Level.Current.LevelType == Level.Type.Battle;
            }
            catch
            {
                return false;
            }
        }

        private static Text MakeText(
            GameObject parent,
            string content,
            int size,
            Color color,
            Vector2 position,
            Vector2 sizeDelta,
            TextAnchor anchor)
        {
            var go = new GameObject("Text_" + content);
            go.transform.SetParent(parent.transform, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = position;
            rt.sizeDelta = sizeDelta;

            var text = go.AddComponent<Text>();
            text.text = content;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = size;
            text.color = color;
            text.alignment = anchor;
            return text;
        }

        private static string FormatTime(float seconds)
        {
            int minutes = Mathf.FloorToInt(seconds / 60f);
            float remainder = seconds - minutes * 60f;
            return minutes.ToString("00") + ":" + remainder.ToString("00.0");
        }

        private static string CleanLevelName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "BATTLE ASSIST";

            string text = value.Replace("level_", string.Empty)
                .Replace("scene_", string.Empty)
                .Replace("_", " ")
                .Trim();

            return text.ToUpperInvariant();
        }
    }
}
