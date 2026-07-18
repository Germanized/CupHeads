using UnityEngine;
using UnityEngine.UI;
using CupheadOnline.Net;

namespace CupheadOnline.UI
{
    /// <summary>
    /// Four canned coordination messages, no voice chat required:
    /// hold C, then press 1–4. The message pops as a themed toast on both
    /// screens, tagged with the sender's character name.
    /// </summary>
    public static class CommWheel
    {
        static readonly string[] Messages = { "WAIT!", "GO!", "REVIVE ME!", "SWELL WORK!" };
        const float TOAST_SECONDS = 2.6f;
        const float SEND_COOLDOWN = 0.8f;

        static float _lastSentAt = -10f;

        // ── Toast surface ─────────────────────────────────────────────────────
        static GameObject _root;
        static CanvasGroup _toastGroup;
        static Text _toastLabel;
        static CanvasGroup _hintGroup;
        static float _toastUntil;

        public static void Tick()
        {
            if (!Plugin.EnableCommWheel || !MultiplayerSession.IsActive
             || Plugin.Net == null || !Plugin.Net.IsConnected)
            {
                SetHintVisible(false);
                UpdateToast();
                return;
            }

            bool wheelHeld = Input.GetKey(KeyCode.C);
            SetHintVisible(wheelHeld);

            if (wheelHeld && Time.unscaledTime - _lastSentAt >= SEND_COOLDOWN)
            {
                int picked = -1;
                if (Input.GetKeyDown(KeyCode.Alpha1)) picked = 0;
                else if (Input.GetKeyDown(KeyCode.Alpha2)) picked = 1;
                else if (Input.GetKeyDown(KeyCode.Alpha3)) picked = 2;
                else if (Input.GetKeyDown(KeyCode.Alpha4)) picked = 3;

                if (picked >= 0)
                {
                    _lastSentAt = Time.unscaledTime;
                    var pkt = new CommMessagePacket
                    {
                        ParticipantId = (byte)MultiplayerSession.LocalId,
                        MessageId = (byte)picked,
                        Tick = MultiplayerSession.Tick,
                    };
                    Plugin.Net.SendCommMessage(ref pkt);
                    ShowToast(MultiplayerSession.GetLocalCharacterName(), picked);
                }
            }

            UpdateToast();
        }

        public static void OnReceived(CommMessagePacket pkt)
        {
            if (!Plugin.EnableCommWheel)
                return;

            string sender;
            if (pkt.ParticipantId <= (byte)PlayerId.PlayerTwo)
                sender = MultiplayerSession.GetCharacterName((PlayerId)pkt.ParticipantId);
            else
                sender = "PLAYER " + (pkt.ParticipantId + 1);

            ShowToast(sender, pkt.MessageId);
        }

        static void ShowToast(string sender, int messageId)
        {
            if (messageId < 0 || messageId >= Messages.Length)
                return;

            EnsureUi();
            if (_toastLabel == null)
                return;

            _toastLabel.text = sender.ToUpperInvariant() + ": " + Messages[messageId];
            _toastUntil = Time.unscaledTime + TOAST_SECONDS;
        }

        static void UpdateToast()
        {
            if (_toastGroup == null)
                return;

            if (Time.unscaledTime < _toastUntil)
            {
                float remaining = _toastUntil - Time.unscaledTime;
                _toastGroup.alpha = Mathf.Clamp01(remaining / 0.35f);
            }
            else
            {
                _toastGroup.alpha = 0f;
            }

            CupheadUiTheme.RefreshLabelFonts(_root);
        }

        static void SetHintVisible(bool visible)
        {
            if (visible)
                EnsureUi();
            if (_hintGroup != null)
                _hintGroup.alpha = visible ? 1f : 0f;
        }

        static void EnsureUi()
        {
            if (_root != null)
                return;

            _root = new GameObject("CupHeads_CommWheel");
            Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 170;

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            // Toast card — top centre, under the boss bars.
            var toast = new GameObject("Toast");
            toast.transform.SetParent(_root.transform, false);
            var toastRt = toast.AddComponent<RectTransform>();
            toastRt.anchorMin = toastRt.anchorMax = new Vector2(0.5f, 1f);
            toastRt.pivot = new Vector2(0.5f, 1f);
            toastRt.anchoredPosition = new Vector2(0f, -170f);
            toastRt.sizeDelta = new Vector2(420f, 56f);
            CupheadUiTheme.StyleCard(toast.AddComponent<Image>(), dark: true);
            _toastGroup = toast.AddComponent<CanvasGroup>();
            _toastGroup.alpha = 0f;
            _toastGroup.interactable = false;
            _toastGroup.blocksRaycasts = false;
            _toastLabel = CupheadUiTheme.MakeLabel(
                toast, string.Empty, 19, CupheadUiTheme.Cream,
                Vector2.zero, new Vector2(390f, 44f),
                TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f));

            // Hint card — bottom centre while C is held.
            var hint = new GameObject("Hint");
            hint.transform.SetParent(_root.transform, false);
            var hintRt = hint.AddComponent<RectTransform>();
            hintRt.anchorMin = hintRt.anchorMax = new Vector2(0.5f, 0f);
            hintRt.pivot = new Vector2(0.5f, 0f);
            hintRt.anchoredPosition = new Vector2(0f, 26f);
            hintRt.sizeDelta = new Vector2(660f, 52f);
            CupheadUiTheme.StyleCard(hint.AddComponent<Image>(), dark: true);
            _hintGroup = hint.AddComponent<CanvasGroup>();
            _hintGroup.alpha = 0f;
            _hintGroup.interactable = false;
            _hintGroup.blocksRaycasts = false;
            CupheadUiTheme.MakeLabel(
                hint, "1 WAIT!   2 GO!   3 REVIVE ME!   4 SWELL WORK!", 15, CupheadUiTheme.Gold,
                Vector2.zero, new Vector2(630f, 40f),
                TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f));
        }
    }
}
