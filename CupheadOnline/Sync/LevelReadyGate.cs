using UnityEngine;
using CupheadOnline.Net;
using CupheadOnline.UI;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Symmetric level-start gate. Whichever peer finishes loading first holds
    /// at the top of the level until the other is in too, so both fights start
    /// on the same beat (boss patterns are time-driven, so a load-time head
    /// start would otherwise desync the whole fight).
    ///
    ///   guest Level.Start → SceneReady → host
    ///   host (both loaded) → LevelSync.Go → guest
    ///
    /// Both sides fail open after a timeout.
    /// </summary>
    public static class LevelReadyGate
    {
        const float WaitTimeout = 10f;
        const float SignalMemory = 30f;

        static bool _holding;
        static int _holdingLevel = int.MinValue;
        static float _holdDeadline;
        static bool _pausedByGate;

        // Host: the guest reported this level loaded.
        static int _guestReadyLevel = int.MinValue;
        static float _guestReadyAt = -1000f;

        // Guest: the host released this level.
        static int _goLevel = int.MinValue;
        static float _goAt = -1000f;

        static LevelReadyGate()
        {
            MultiplayerSession.OnSessionEnded += ResetInternal;
        }

        public static bool IsHolding => _holding;

        static bool Active =>
            Plugin.EnableLevelReadyGate
            && MultiplayerSession.IsActive
            && !LocalDevSession.IsActive
            && Plugin.Net != null
            && Plugin.Net.IsConnected;

        /// <summary>Called from the Level.Start postfix on both peers.</summary>
        public static void OnLevelStarted()
        {
            if (!Active || Level.Current == null)
                return;

            int levelEnum;
            try { levelEnum = (int)Level.Current.CurrentLevel; }
            catch { return; }

            if (MultiplayerSession.IsClient)
            {
                var ready = new SceneReadyPacket
                {
                    LevelEnum = levelEnum,
                    Tick = MultiplayerSession.Tick,
                };
                Plugin.Net.SendSceneReady(ref ready);
                Plugin.Log.LogInfo("[ReadyGate] Sent scene-ready for level " + levelEnum + ".");

                if (IsRecent(_goLevel, _goAt, levelEnum))
                {
                    Plugin.Log.LogInfo("[ReadyGate] Host already released level " + levelEnum + " - no hold.");
                    return;
                }

                Hold(levelEnum, "Waiting for your partner to load in...");
                return;
            }

            if (IsRecent(_guestReadyLevel, _guestReadyAt, levelEnum))
            {
                Plugin.Log.LogInfo("[ReadyGate] Guest already loaded level " + levelEnum + " - starting together.");
                SendGo(levelEnum);
                return;
            }

            Hold(levelEnum, "Waiting for your partner to load in...");
        }

        /// <summary>Host: the guest's copy of a level finished loading.</summary>
        public static void OnGuestSceneReady(SceneReadyPacket pkt)
        {
            if (!MultiplayerSession.IsHost)
                return;

            _guestReadyLevel = pkt.LevelEnum;
            _guestReadyAt = Time.unscaledTime;

            int currentLevel = CurrentLevelEnum();
            if (currentLevel != pkt.LevelEnum)
                return;

            // Either we were holding for them, or we are already running and the
            // guest is the late one; both cases release the guest right away.
            if (_holding)
                Release("Partner loaded in - have at it!");
            SendGo(pkt.LevelEnum);
        }

        /// <summary>Guest: the host released the level start.</summary>
        public static void OnHostGo(int levelEnum)
        {
            if (!MultiplayerSession.IsClient)
                return;

            _goLevel = levelEnum;
            _goAt = Time.unscaledTime;

            if (_holding && _holdingLevel == levelEnum)
                Release("Partner ready - have at it!");
        }

        public static void Update()
        {
            if (!_holding)
                return;

            if (!Active || Level.Current == null)
            {
                Release(null);
                return;
            }

            if (Time.unscaledTime >= _holdDeadline)
            {
                Release("Continuing without partner confirmation.");
                if (MultiplayerSession.IsHost)
                    SendGo(_holdingLevel);
            }
        }

        /// <summary>A new scene load started: forget signals that belong to the previous attempt.</summary>
        public static void OnSceneLoadStarted()
        {
            _guestReadyLevel = int.MinValue;
            _goLevel = int.MinValue;
        }

        public static void OnSceneChanged()
        {
            // Never leave a stale pause behind a scene transition.
            if (_holding || _pausedByGate)
                Release(null);
        }

        static void Hold(int levelEnum, string message)
        {
            _holding = true;
            _holdingLevel = levelEnum;
            _holdDeadline = Time.unscaledTime + WaitTimeout;
            TrySetPaused(true);
            ConnectionHUD.Show(message);
            Plugin.Log.LogInfo("[ReadyGate] Holding level " + levelEnum + " start (timeout " + WaitTimeout + "s).");
        }

        static void Release(string message)
        {
            _holding = false;
            _holdingLevel = int.MinValue;
            TrySetPaused(false);
            if (!string.IsNullOrEmpty(message))
                ConnectionHUD.Show(message);
            Plugin.Log.LogInfo("[ReadyGate] Released." + (string.IsNullOrEmpty(message) ? string.Empty : " " + message));
        }

        static void SendGo(int levelEnum)
        {
            if (Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            var pkt = new LevelSyncPacket
            {
                Kind = (byte)LevelSyncKind.Go,
                LevelEnum = levelEnum,
                Tick = MultiplayerSession.Tick,
            };
            Plugin.Net.SendLevelSync(ref pkt);
        }

        static bool IsRecent(int signalLevel, float signalAt, int levelEnum)
        {
            return signalLevel == levelEnum && Time.unscaledTime - signalAt <= SignalMemory;
        }

        static int CurrentLevelEnum()
        {
            try { return Level.Current != null ? (int)Level.Current.CurrentLevel : int.MinValue; }
            catch { return int.MinValue; }
        }

        static void TrySetPaused(bool paused)
        {
            try
            {
                if (paused)
                {
                    if (PauseManager.state != PauseManager.State.Paused)
                    {
                        PauseManager.Pause();
                        _pausedByGate = true;
                    }
                }
                else if (_pausedByGate)
                {
                    _pausedByGate = false;
                    if (PauseManager.state == PauseManager.State.Paused)
                        PauseManager.Unpause();
                }
            }
            catch (System.Exception ex)
            {
                _pausedByGate = false;
                Plugin.Log.LogWarning("[ReadyGate] Pause toggle failed: " + ex.Message);
            }
        }

        static void ResetInternal()
        {
            if (_holding || _pausedByGate)
                Release(null);
            _guestReadyLevel = int.MinValue;
            _guestReadyAt = -1000f;
            _goLevel = int.MinValue;
            _goAt = -1000f;
        }
    }
}
