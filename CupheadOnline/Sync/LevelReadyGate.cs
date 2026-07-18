using UnityEngine;
using CupheadOnline.Net;
using CupheadOnline.UI;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Holds the host at the top of a level until the guest confirms their copy
    /// of the scene finished loading, so one player is never fighting while the
    /// other is still behind the loading iris. Fails open on a timeout.
    /// </summary>
    public static class LevelReadyGate
    {
        const float WAIT_TIMEOUT = 10f;
        const float READY_MEMORY = 30f;

        static bool _hostWaiting;
        static float _waitDeadline;
        static bool _pausedByGate;

        static int _lastGuestReadyLevel = int.MinValue;
        static float _lastGuestReadyAt = -1000f;

        static LevelReadyGate()
        {
            MultiplayerSession.OnSessionEnded += ResetInternal;
        }

        /// <summary>Called from the Level.Start postfix on both peers.</summary>
        public static void OnLevelStarted()
        {
            if (!Plugin.EnableLevelReadyGate)
                return;
            if (!MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;
            if (Level.Current == null)
                return;

            int levelEnum;
            try { levelEnum = (int)Level.Current.CurrentLevel; }
            catch { return; }

            if (MultiplayerSession.IsClient)
            {
                var pkt = new SceneReadyPacket
                {
                    LevelEnum = levelEnum,
                    Tick = MultiplayerSession.Tick,
                };
                Plugin.Net.SendSceneReady(ref pkt);
                Plugin.Log.LogInfo("[ReadyGate] Sent scene-ready for level " + levelEnum + ".");
                return;
            }

            // Host: skip the gate if the guest already reported this level ready.
            if (_lastGuestReadyLevel == levelEnum
             && Time.unscaledTime - _lastGuestReadyAt <= READY_MEMORY)
            {
                Plugin.Log.LogInfo("[ReadyGate] Guest already ready for level " + levelEnum + " - no hold.");
                return;
            }

            _hostWaiting = true;
            _waitDeadline = Time.unscaledTime + WAIT_TIMEOUT;
            TrySetPaused(true);
            ConnectionHUD.Show("Waiting for your partner to load in...");
            Plugin.Log.LogInfo("[ReadyGate] Holding level start for guest (timeout " + WAIT_TIMEOUT + "s).");
        }

        /// <summary>Host-side: dispatcher delivers the guest's scene-ready signal.</summary>
        public static void OnGuestSceneReady(SceneReadyPacket pkt)
        {
            if (!MultiplayerSession.IsHost)
                return;

            _lastGuestReadyLevel = pkt.LevelEnum;
            _lastGuestReadyAt = Time.unscaledTime;

            if (!_hostWaiting)
                return;

            Release("Partner loaded in - have at it!");
        }

        public static void Update()
        {
            if (!_hostWaiting)
                return;

            if (!MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
            {
                Release(null);
                return;
            }

            if (Time.unscaledTime >= _waitDeadline)
                Release("Continuing without partner confirmation.");
        }

        public static void OnSceneChanged()
        {
            // Never leave a stale pause behind a scene transition.
            if (_hostWaiting)
                Release(null);
        }

        static void Release(string message)
        {
            _hostWaiting = false;
            TrySetPaused(false);
            if (!string.IsNullOrEmpty(message))
                ConnectionHUD.Show(message);
            Plugin.Log.LogInfo("[ReadyGate] Released." + (string.IsNullOrEmpty(message) ? string.Empty : " " + message));
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
            if (_hostWaiting || _pausedByGate)
                Release(null);
            _lastGuestReadyLevel = int.MinValue;
            _lastGuestReadyAt = -1000f;
        }
    }
}
