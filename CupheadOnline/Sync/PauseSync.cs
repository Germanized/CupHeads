using CupheadOnline.Net;
using CupheadOnline.UI;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Shared in-level pause, like local co-op: when either player opens the
    /// level pause menu, both worlds freeze. Without it the paused peer's boss
    /// timeline stops while the other keeps running, and the two simulations
    /// drift apart for the rest of the fight.
    ///
    /// The partner's world is frozen with PauseManager directly (no menu is
    /// shown there), and a player who closes their own menu while the partner's
    /// is still open stays frozen until the partner resumes too.
    /// </summary>
    public static class PauseSync
    {
        static bool _localMenuOpen;
        static bool _remotePaused;
        static bool _frozenForRemote;

        static PauseSync()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static bool IsFrozenForPartner => _frozenForRemote;

        static bool Enabled =>
            Plugin.EnableSharedPause
            && MultiplayerSession.IsActive
            && !LocalDevSession.IsActive
            && Plugin.Net != null
            && Plugin.Net.IsConnected;

        static bool InPlayableLevel
        {
            get
            {
                var level = Level.Current;
                return level != null && level.Started && !level.Ending && !LevelReadyGate.IsHolding;
            }
        }

        /// <summary>Postfix of AbstractPauseGUI.PauseGameplay.</summary>
        public static void OnLocalPauseMenuOpened(AbstractPauseGUI gui)
        {
            if (!(gui is LevelPauseGUI) || !Enabled || Level.Current == null)
                return;

            _localMenuOpen = true;
            Send(SessionSignalKind.PauseOn);
        }

        /// <summary>Prefix of AbstractPauseGUI.UnpauseGameplay. False keeps the world frozen.</summary>
        public static bool OnLocalPauseMenuClosing(AbstractPauseGUI gui)
        {
            if (!(gui is LevelPauseGUI) || !_localMenuOpen)
                return true;

            _localMenuOpen = false;
            if (!Enabled)
                return true;

            Send(SessionSignalKind.PauseOff);
            if (!_remotePaused)
                return true;

            _frozenForRemote = true;
            ConnectionHUD.Show("Waiting for your partner to unpause...");
            return false;
        }

        /// <summary>The partner opened or closed their pause menu.</summary>
        public static void OnRemotePause(bool paused)
        {
            _remotePaused = paused;
            if (!Enabled || Level.Current == null)
                return;

            if (paused)
            {
                if (!InPlayableLevel || PauseManager.state == PauseManager.State.Paused)
                    return;

                PauseManager.Pause();
                _frozenForRemote = true;
                ConnectionHUD.Show("Your partner paused the game.");
                return;
            }

            if (_frozenForRemote && !_localMenuOpen)
                Resume("Your partner resumed - let's go!");
        }

        public static void Update()
        {
            if (!_frozenForRemote)
                return;

            // Never leave a world frozen for a partner who is gone.
            if (!Enabled || Level.Current == null)
                Resume(null);
        }

        public static void OnSceneChanged()
        {
            if (_frozenForRemote)
                Resume(null);

            _localMenuOpen = false;
            _remotePaused = false;
        }

        static void Reset()
        {
            OnSceneChanged();
        }

        static void Resume(string message)
        {
            _frozenForRemote = false;
            try
            {
                if (PauseManager.state == PauseManager.State.Paused)
                    PauseManager.Unpause();
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("[PauseSync] Resume failed: " + ex.Message);
            }

            if (!string.IsNullOrEmpty(message))
                ConnectionHUD.Show(message);
        }

        static void Send(SessionSignalKind kind)
        {
            if (Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            var pkt = new SessionSignalPacket { Signal = (byte)kind };
            Plugin.Net.SendSessionSignal(ref pkt);
        }
    }
}
