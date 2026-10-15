using System;
using System.Reflection;
using CupheadOnline.Net;
using CupheadOnline.UI;
using HarmonyLib;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Host-authoritative knockout.
    ///
    /// Every level win in Cuphead funnels through Level.zHack_OnWin (bosses wire
    /// it to properties.OnBossDeath, platforming levels call it from the exit).
    /// The host announces its win; the guest's copy may only win once the host
    /// has, so the guest can never reach KNOCKOUT, save progress, and run the
    /// results flow while the host is still fighting. If the guest's local boss
    /// dies first, its win is parked until the host's announcement arrives
    /// (with a fail-open timeout so a lost host never strands the guest).
    /// </summary>
    public static class LevelOutcomeSync
    {
        const float GuestFailOpenSeconds = 12f;
        const float HostWinBackstopSeconds = 4f;

        static readonly MethodInfo ZHackOnWinMethod = AccessTools.Method(typeof(Level), "zHack_OnWin");

        static int _hostWonLevel = int.MinValue;
        static float _hostWonAt;
        static int _announcedLevel = int.MinValue;
        static bool _localWinParked;
        static float _localWinParkedAt;
        static bool _forcingLocalWin;

        static LevelOutcomeSync()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static bool IsLocalWinParked => _localWinParked;

        static bool GuestGateActive =>
            MultiplayerSession.IsClient
            && !LocalDevSession.IsActive
            && Plugin.Net != null
            && Plugin.Net.IsConnected;

        /// <summary>Prefix of Level.zHack_OnWin. False parks the guest's win.</summary>
        public static bool AllowWin(Level level)
        {
            if (level == null)
                return true;

            int levelEnum = GetLevelEnum(level);

            if (MultiplayerSession.IsHost)
            {
                AnnounceHostWin(levelEnum);
                return true;
            }

            if (!GuestGateActive || _forcingLocalWin || _hostWonLevel == levelEnum)
            {
                _localWinParked = false;
                return true;
            }

            if (!_localWinParked)
            {
                _localWinParked = true;
                _localWinParkedAt = Time.unscaledTime;
                Plugin.Log.LogInfo("[Outcome] Local knockout parked until the host confirms level " + levelEnum + ".");
            }

            return false;
        }

        /// <summary>Guest: the host's copy of this level was won.</summary>
        public static void OnHostLevelWon(int levelEnum)
        {
            if (!MultiplayerSession.IsClient)
                return;

            _hostWonLevel = levelEnum;
            _hostWonAt = Time.unscaledTime;
            _backstopUsed = false;

            var level = Level.Current;
            if (level == null || GetLevelEnum(level) != levelEnum || Level.Won)
                return;

            if (_localWinParked)
            {
                Plugin.Log.LogInfo("[Outcome] Host confirmed the knockout - releasing the parked win.");
                ForceLocalWin(level);
                return;
            }

            // Our boss is still standing: finish it through the game's own damage
            // path so the boss death animation and the win both play normally.
            if (level.LevelType == Level.Type.Battle && EnemySyncManager.TryForceBossDefeat())
            {
                Plugin.Log.LogInfo("[Outcome] Host won first - finished the local boss to match.");
                return;
            }

            // Platforming and scripted levels: the host's upcoming scene change
            // pulls this peer along; nothing to force here.
        }

        public static void Update()
        {
            if (!_localWinParked)
            {
                CheckHostWinBackstop();
                return;
            }

            var level = Level.Current;
            if (level == null || Level.Won)
            {
                _localWinParked = false;
                return;
            }

            if (GuestGateActive && Time.unscaledTime - _localWinParkedAt < GuestFailOpenSeconds)
                return;

            Plugin.Log.LogWarning("[Outcome] Host knockout never arrived - completing the local win.");
            ForceLocalWin(level);
        }

        /// <summary>
        /// The host won but nothing on this side completed the level (a
        /// scripted or platforming finish that never triggered locally, or a
        /// boss whose properties could not be reached). Record the win anyway
        /// so the guest's save keeps pace with the host's.
        /// </summary>
        static void CheckHostWinBackstop()
        {
            if (_backstopUsed || _hostWonLevel == int.MinValue || !MultiplayerSession.IsClient)
                return;
            if (Time.unscaledTime - _hostWonAt < HostWinBackstopSeconds)
                return;

            _backstopUsed = true; // one attempt per announcement
            var level = Level.Current;
            if (level == null || Level.Won || GetLevelEnum(level) != _hostWonLevel)
                return;

            Plugin.Log.LogInfo("[Outcome] Host won " + _hostWonLevel + " but the local level never finished - completing it.");
            ForceLocalWin(level);
        }

        static bool _backstopUsed;

        public static void OnSceneChanged()
        {
            _backstopUsed = false;
            _hostWonLevel = int.MinValue;
            _announcedLevel = int.MinValue;
            _localWinParked = false;
        }

        static void Reset()
        {
            OnSceneChanged();
        }

        static void AnnounceHostWin(int levelEnum)
        {
            if (Plugin.Net == null || !Plugin.Net.IsConnected || _announcedLevel == levelEnum)
                return;

            _announcedLevel = levelEnum;
            var pkt = new LevelSyncPacket
            {
                Kind = (byte)LevelSyncKind.Won,
                LevelEnum = levelEnum,
                Tick = MultiplayerSession.Tick,
            };
            Plugin.Net.SendLevelSync(ref pkt);
            Plugin.Log.LogInfo("[Outcome] Announced host knockout for level " + levelEnum + ".");
        }

        static void ForceLocalWin(Level level)
        {
            _localWinParked = false;
            if (ZHackOnWinMethod == null)
                return;

            _forcingLocalWin = true;
            try
            {
                ZHackOnWinMethod.Invoke(level, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Outcome] Could not complete the local win: " + (ex.InnerException ?? ex).Message);
                ConnectionHUD.Show("Knockout sync failed - follow the host when it continues.");
            }
            finally
            {
                _forcingLocalWin = false;
            }
        }

        static int GetLevelEnum(Level level)
        {
            try { return (int)level.CurrentLevel; }
            catch { return int.MinValue; }
        }
    }
}
