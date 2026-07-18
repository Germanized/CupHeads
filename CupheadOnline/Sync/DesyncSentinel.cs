using UnityEngine;
using CupheadOnline.Net;
using CupheadOnline.UI;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Turns "it feels off" into a measured, self-healing event. The host
    /// periodically samples rough state (boss HP, both player positions); the
    /// guest compares against its own simulation and, after two consecutive
    /// mismatches, force-corrects boss HP and requests a recovery burst.
    /// </summary>
    public static class DesyncSentinel
    {
        const float SAMPLE_INTERVAL = 2f;
        const float POSITION_TOLERANCE = 5f;
        const float BOSS_HP_TOLERANCE_ABS = 60f;
        const float BOSS_HP_TOLERANCE_PCT = 0.08f;
        const int STRIKES_TO_ACT = 2;
        const float ACT_COOLDOWN = 10f;

        static float _nextSampleAt;
        static int _mismatchStreak;
        static float _lastActionAt = -1000f;

        static DesyncSentinel()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static void Update()
        {
            if (!Plugin.EnableDesyncSentinel)
                return;
            if (!MultiplayerSession.IsHost || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;
            if (Level.Current == null)
                return;
            if (Time.unscaledTime < _nextSampleAt)
                return;

            _nextSampleAt = Time.unscaledTime + SAMPLE_INTERVAL;

            var pkt = new StateHashPacket { Tick = MultiplayerSession.Tick };

            float bossHp;
            if (EnemySyncManager.TryGetLocalBossHp(out bossHp))
            {
                pkt.BossHp = bossHp;
                pkt.Flags |= 1;
            }

            Vector2 pos;
            if (TryGetPlayerPosition(PlayerId.PlayerOne, out pos))
            {
                pkt.P1X = pos.x;
                pkt.P1Y = pos.y;
                pkt.Flags |= 2;
            }

            if (TryGetPlayerPosition(PlayerId.PlayerTwo, out pos))
            {
                pkt.P2X = pos.x;
                pkt.P2Y = pos.y;
                pkt.Flags |= 4;
            }

            if (pkt.Flags != 0)
                Plugin.Net.SendStateHash(ref pkt);
        }

        /// <summary>Guest-side: compare the host's sample against local state.</summary>
        public static void OnHostSample(StateHashPacket pkt)
        {
            if (!Plugin.EnableDesyncSentinel || !MultiplayerSession.IsClient)
                return;
            if (Level.Current == null)
                return;

            bool mismatch = false;

            float localBossHp;
            if (pkt.HasBossHp && EnemySyncManager.TryGetLocalBossHp(out localBossHp))
            {
                float tolerance = Mathf.Max(BOSS_HP_TOLERANCE_ABS, pkt.BossHp * BOSS_HP_TOLERANCE_PCT);
                if (Mathf.Abs(localBossHp - pkt.BossHp) > tolerance)
                    mismatch = true;
            }

            // Only judge the host-owned proxy (P1 on the guest); the guest's own
            // player legitimately runs ahead of the host's view of it.
            Vector2 localPos;
            if (pkt.HasP1 && TryGetPlayerPosition(PlayerId.PlayerOne, out localPos))
            {
                if (Vector2.Distance(localPos, new Vector2(pkt.P1X, pkt.P1Y)) > POSITION_TOLERANCE)
                    mismatch = true;
            }

            if (!mismatch)
            {
                _mismatchStreak = 0;
                return;
            }

            _mismatchStreak++;
            if (_mismatchStreak < STRIKES_TO_ACT)
                return;
            if (Time.unscaledTime - _lastActionAt < ACT_COOLDOWN)
                return;

            _mismatchStreak = 0;
            _lastActionAt = Time.unscaledTime;
            Plugin.Log.LogWarning("[DesyncSentinel] Repeated state mismatch - auto-correcting and requesting resync.");
            ConnectionHUD.Show("Desync detected - auto-resyncing...");

            if (pkt.HasBossHp)
                EnemySyncManager.ForceLocalBossHp(pkt.BossHp);
            SessionSync.RequestRecovery();
        }

        static bool TryGetPlayerPosition(PlayerId id, out Vector2 position)
        {
            position = Vector2.zero;
            try
            {
                var player = PlayerManager.GetPlayer(id);
                if (player == null || player.IsDead || player.gameObject == null || !player.gameObject.activeInHierarchy)
                    return false;

                position = player.transform.position;
                return true;
            }
            catch
            {
                return false;
            }
        }

        static void Reset()
        {
            _nextSampleAt = 0f;
            _mismatchStreak = 0;
            _lastActionAt = -1000f;
        }
    }
}
