using System.Reflection;
using CupheadOnline.Net;
using HarmonyLib;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Map equip menu (the two flip cards).
    ///
    /// Both machines run the menu with both players' input, so the vanilla
    /// co-op rule works online too: each player edits their own card, and the
    /// menu closes once both have backed out (the first one to back out waits
    /// on the "ready" side). The previous build forced both cards ready on any
    /// close, so the first player to press Back yanked the menu away from a
    /// partner who was still choosing.
    ///
    /// As a safety net the host reports whether its equip menu is open. If the
    /// guest's menu is open while the host's has been closed for a moment (or
    /// the other way round) — e.g. the menu opened on only one machine because
    /// a map prompt was up there — the guest follows the host.
    /// </summary>
    public static class EquipMenuSync
    {
        const float HostHeartbeat = 1f;
        // A disagreement must settle before the guest follows the host: the
        // press that opened/closed one side is still in flight to the other
        // for about one round trip.
        const float OpenGrace = 0.35f;
        const float LocalSettleBeforeOpen = 1f;
        const float CloseGrace = 1.5f;

        static readonly FieldInfo PlayerOneCardField = AccessTools.Field(typeof(AbstractEquipUI), "playerOne");
        static readonly FieldInfo PlayerTwoCardField = AccessTools.Field(typeof(AbstractEquipUI), "playerTwo");
        static readonly PropertyInfo CanPauseProperty = AccessTools.Property(typeof(AbstractPauseGUI), "CanPause");

        // Host
        static bool _hostLastSentOpen;
        static float _hostNextHeartbeatAt;

        // Guest
        static bool _hostKnown;
        static bool _hostOpen;
        static float _hostStateSince;
        static bool _localOpen;
        static float _localStateSince;

        static EquipMenuSync()
        {
            MultiplayerSession.OnSessionEnded += OnSceneChanged;
        }

        public static void Update()
        {
            if (!MultiplayerSession.IsActive || LocalDevSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;
            if (Map.Current == null)
                return;

            var equip = AbstractEquipUI.Current as MapEquipUI;
            if (equip == null)
                return;

            bool localOpen = equip.CurrentState == AbstractEquipUI.ActiveState.Active;

            if (MultiplayerSession.IsHost)
            {
                float now = Time.unscaledTime;
                if (localOpen != _hostLastSentOpen || now >= _hostNextHeartbeatAt)
                {
                    _hostLastSentOpen = localOpen;
                    _hostNextHeartbeatAt = now + HostHeartbeat;
                    var pkt = new LevelSyncPacket
                    {
                        Kind = (byte)LevelSyncKind.EquipMenu,
                        LevelEnum = localOpen ? 1 : 0,
                        Tick = MultiplayerSession.Tick,
                    };
                    Plugin.Net.SendLevelSync(ref pkt);
                }
                return;
            }

            float nowGuest = Time.unscaledTime;
            if (localOpen != _localOpen)
            {
                _localOpen = localOpen;
                _localStateSince = nowGuest;
            }

            if (!_hostKnown)
                return;

            float hostHeld = nowGuest - _hostStateSince;
            float localHeld = nowGuest - _localStateSince;
            if (_hostOpen && !localOpen && hostHeld >= OpenGrace && localHeld >= LocalSettleBeforeOpen && CanOpen(equip))
            {
                Plugin.Log.LogInfo("[EquipSync] Host has the equip menu open - opening it here too.");
                equip.StartCoroutine(equip.ShowPauseMenu());
            }
            else if (!_hostOpen && localOpen && hostHeld >= CloseGrace && localHeld >= CloseGrace)
            {
                Plugin.Log.LogInfo("[EquipSync] Host closed the equip menu - closing it here too.");
                ForceClose(equip);
            }
        }

        /// <summary>Guest: the host's equip menu state.</summary>
        public static void OnHostEquipMenu(bool open)
        {
            if (!_hostKnown || open != _hostOpen)
                _hostStateSince = Time.unscaledTime;

            _hostKnown = true;
            _hostOpen = open;
        }

        public static void OnSceneChanged()
        {
            _hostKnown = false;
            _hostOpen = false;
            _hostLastSentOpen = false;
            _hostNextHeartbeatAt = 0f;
            _localOpen = false;
            _localStateSince = 0f;
        }

        static bool CanOpen(AbstractEquipUI equip)
        {
            if (PauseManager.state == PauseManager.State.Paused)
                return false;

            try
            {
                return CanPauseProperty == null || (bool)CanPauseProperty.GetValue(equip, null);
            }
            catch
            {
                return false;
            }
        }

        static void ForceClose(AbstractEquipUI equip)
        {
            MarkReady(PlayerOneCardField != null ? PlayerOneCardField.GetValue(equip) : null);
            MarkReady(PlayerTwoCardField != null ? PlayerTwoCardField.GetValue(equip) : null);
            try { equip.Close(); }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[EquipSync] Close failed: " + ex.Message); }
        }

        static void MarkReady(object card)
        {
            var equipCard = card as MapEquipUICard;
            if (equipCard != null)
                Traverse.Create(equipCard).Property("ReadyAndWaiting").SetValue(true);
        }
    }
}
