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
        static Image _toastCard;
        static CanvasGroup _hintGroup;
        static Image _hintCard;
        static float _toastUntil;

        public static void Tick()
        {
            if (!Plugin.EnableCommWheel || !MultiplayerSession.IsActive
             || Plugin.Net == null || !Plugin.Net.IsConnected)
            {
                SetHintVisible(UiPreview.Overlays);
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

        /// <summary>UiPreview: a long-lived sample message.</summary>
        public static void ShowPreviewToast()
        {
            ShowToast(UiPreview.PartnerCharacter, 1);
            _toastUntil = Time.unscaledTime + 30f;
        }

        static void ShowToast(string sender, int messageId)
        {
            if (messageId < 0 || messageId >= Messages.Length)
                return;

            EnsureUi();
            if (_toastLabel == null)
                return;

            // Sender in the menu red, message in ink — like a printed telegram.
            _toastLabel.text = "<color=#AD1C1C>" + sender.ToUpperInvariant() + ":</color> " + Messages[messageId];
            _toastUntil = Time.unscaledTime + TOAST_SECONDS;
        }

        static void UpdateToast()
        {
            if (_toastGroup == null)
                return;

            NativeUiKit.ApplyCard(_toastCard, false);
            NativeUiKit.ApplyCard(_hintCard, false);

            if (Time.unscaledTime < _toastUntil)
            {
                float remaining = _toastUntil - Time.unscaledTime;
                _toastGroup.alpha = Mathf.Clamp01(remaining / 0.35f);
            }
            else
            {
                _toastGroup.alpha = 0f;
            }
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

            _root = NativeUiKit.MakeOverlayCanvas("CupHeads_CommWheel", 170);

            // Toast — a paper slip at top centre, under the boss bars.
            _toastCard = NativeUiKit.MakeCard(_root, "Toast", Vector2.zero, new Vector2(520f, 82f), false, null);
            var toastRt = _toastCard.rectTransform;
            toastRt.anchorMin = toastRt.anchorMax = new Vector2(0.5f, 1f);
            toastRt.pivot = new Vector2(0.5f, 1f);
            toastRt.anchoredPosition = new Vector2(0f, -170f);
            _toastGroup = _toastCard.gameObject.AddComponent<CanvasGroup>();
            _toastGroup.alpha = 0f;
            _toastGroup.interactable = false;
            _toastGroup.blocksRaycasts = false;
            _toastLabel = NativeUiKit.MakeText(_toastCard.gameObject, "Label", FontRole.Vogue, 30, NativeUiKit.Ink,
                TextAnchor.MiddleCenter, new Vector2(0f, 1f), new Vector2(480f, 50f));
            _toastLabel.horizontalOverflow = HorizontalWrapMode.Overflow;

            // Hint — a paper strip at bottom centre while C is held.
            _hintCard = NativeUiKit.MakeCard(_root, "Hint", Vector2.zero, new Vector2(900f, 78f), false, null);
            var hintRt = _hintCard.rectTransform;
            hintRt.anchorMin = hintRt.anchorMax = new Vector2(0.5f, 0f);
            hintRt.pivot = new Vector2(0.5f, 0f);
            hintRt.anchoredPosition = new Vector2(0f, 30f);
            _hintGroup = _hintCard.gameObject.AddComponent<CanvasGroup>();
            _hintGroup.alpha = 0f;
            _hintGroup.interactable = false;
            _hintGroup.blocksRaycasts = false;
            var hintLabel = NativeUiKit.MakeText(_hintCard.gameObject, "Label", FontRole.Vogue, 26, NativeUiKit.Ink,
                TextAnchor.MiddleCenter, new Vector2(0f, 1f), new Vector2(860f, 44f));
            hintLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
            hintLabel.text = "<color=#AD1C1C>1</color> WAIT!     <color=#AD1C1C>2</color> GO!     "
                + "<color=#AD1C1C>3</color> REVIVE ME!     <color=#AD1C1C>4</color> SWELL WORK!";
        }
    }
}
