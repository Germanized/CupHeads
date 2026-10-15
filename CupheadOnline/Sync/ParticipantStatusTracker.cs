using System.Collections.Generic;
using System.Text;
using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.Sync
{
    public static class ParticipantStatusTracker
    {
        public struct ParticipantStatus
        {
            public byte ParticipantId;
            public byte Health;
            public byte HealthMax;
            public bool IsDead;
            public bool CanDonate;
            public bool IsChalice;
            public bool IsMugman;
            public bool IsKnown;
            public uint Tick;
        }

        static readonly Dictionary<byte, ParticipantStatus> _statuses =
            new Dictionary<byte, ParticipantStatus>(6);
        static readonly List<byte> _sortedIds =
            new List<byte>(6);

        static ParticipantStatusTracker()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static void Reset()
        {
            _statuses.Clear();
            ResetOwnedStatus();
        }

        public static void Apply(PlayerStatusPacket pkt)
        {
            ParticipantStatus existing;
            if (_statuses.TryGetValue(pkt.ParticipantId, out existing)
             && NetTick.IsOlder(pkt.Tick, existing.Tick))
            {
                return;
            }

            _statuses[pkt.ParticipantId] = new ParticipantStatus
            {
                ParticipantId = pkt.ParticipantId,
                Health = pkt.Health,
                HealthMax = pkt.HealthMax,
                IsDead = pkt.IsDead,
                CanDonate = pkt.CanDonate,
                IsChalice = pkt.IsChalice,
                IsMugman = pkt.IsMugman,
                IsKnown = true,
                Tick = pkt.Tick,
            };
        }

        public static bool TryGet(byte participantId, out ParticipantStatus status)
        {
            if (_statuses.TryGetValue(participantId, out status))
                return true;

            if (participantId <= (byte)PlayerId.PlayerTwo)
            {
                var player = GetPlayerSafe((PlayerId)participantId);
                if (player != null && player.stats != null)
                {
                    status = new ParticipantStatus
                    {
                        ParticipantId = participantId,
                        Health = (byte)Mathf.Clamp(player.stats.Health, 0, 255),
                        HealthMax = (byte)Mathf.Clamp(player.stats.HealthMax, 1, 255),
                        IsDead = player.IsDead,
                        CanDonate = player.stats.PartnerCanSteal && !player.IsDead,
                        IsChalice = player.stats.isChalice,
                        IsMugman = GetCharacterName(player) == "Mugman",
                        IsKnown = true,
                        Tick = MultiplayerSession.Tick,
                    };
                    return true;
                }
            }

            status = default(ParticipantStatus);
            return false;
        }

        public static bool TryBuildLocalPacket(AbstractPlayerController player, out PlayerStatusPacket pkt)
        {
            pkt = default(PlayerStatusPacket);
            if (player == null || player.stats == null)
                return false;

            int health = Mathf.Clamp(player.stats.Health, 0, 255);
            int healthMax = Mathf.Clamp(player.stats.HealthMax, 0, 255);
            byte flags = 0;
            if (player.IsDead) flags |= 1;
            if (player.stats.PartnerCanSteal && !player.IsDead) flags |= 2;
            string characterName = GetCharacterName(player);
            if (player.stats.isChalice) flags |= 4;
            if (characterName == "Mugman") flags |= 8;
            if (Level.Current != null) flags |= 16;

            pkt = new PlayerStatusPacket
            {
                ParticipantId = (byte)player.id,
                Health = (byte)health,
                HealthMax = (byte)Mathf.Max(healthMax, 1),
                Flags = flags,
                Tick = MultiplayerSession.Tick,
                SuperMeter = player.stats.SuperMeter,
            };
            return true;
        }

        // ── Owner-authoritative stats ─────────────────────────────────────────
        //
        // Each machine fills a player's super meter from the hits *it*
        // simulated, so the two copies of a player drifted apart: an EX or super
        // the owner could fire did nothing on the other machine (or vice versa).
        // The owner of a body (see MultiplayerSession.IsBodyOwnedLocally) now
        // streams its meter, and the other machine adopts it. Health only ever
        // moves up this way (heals, revives, missed absorbs); losses still arrive
        // as the owner's replicated hits, so a status that overtakes its hit can
        // never apply the same damage twice.

        static readonly float[] LastSentMeter = { -1f, -1f };
        static readonly int[] LastSentHealth = { -1, -1 };
        static readonly float[] NextSendAt = { 0f, 0f };
        static readonly float[] NextHeartbeatAt = { 0f, 0f };

        static readonly System.Reflection.MethodInfo OnSuperChangedMethod =
            HarmonyLib.AccessTools.Method(typeof(PlayerStatsManager), "OnSuperChanged");
        static readonly System.Reflection.MethodInfo OnHealthChangedMethod =
            HarmonyLib.AccessTools.Method(typeof(PlayerStatsManager), "OnHealthChanged");
        // Both properties have private setters.
        static readonly System.Reflection.MethodInfo SuperMeterSetter =
            HarmonyLib.AccessTools.PropertySetter(typeof(PlayerStatsManager), "SuperMeter");
        static readonly System.Reflection.MethodInfo HealthSetter =
            HarmonyLib.AccessTools.PropertySetter(typeof(PlayerStatsManager), "Health");

        /// <summary>Per frame: stream owned bodies' meter/health while it changes, plus a heartbeat.</summary>
        public static void TickOwnedStatus()
        {
            if (!MultiplayerSession.IsActive || LocalDevSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;
            if (Level.Current == null)
                return;

            float now = Time.unscaledTime;
            for (int i = 0; i < 2; i++)
            {
                var id = (PlayerId)i;
                if (!MultiplayerSession.IsBodyOwnedLocally(id))
                    continue;

                var player = GetPlayerSafe(id);
                if (player == null || player.stats == null)
                    continue;

                float meter = player.stats.SuperMeter;
                int health = player.stats.Health;
                bool changed = Mathf.Abs(meter - LastSentMeter[i]) >= 0.25f || health != LastSentHealth[i];
                if (!(changed && now >= NextSendAt[i]) && now < NextHeartbeatAt[i])
                    continue;

                LastSentMeter[i] = meter;
                LastSentHealth[i] = health;
                NextSendAt[i] = now + 0.1f;
                NextHeartbeatAt[i] = now + 1f;
                PushLocalStatus(player);
            }
        }

        /// <summary>A status from the peer that owns this body: adopt its meter (and heals).</summary>
        public static void ApplyOwnerStats(PlayerStatusPacket pkt)
        {
            if (!pkt.HasLevelStats || pkt.IsDead || pkt.ParticipantId > (byte)PlayerId.PlayerTwo)
                return;
            if (Level.Current == null || LocalDevSession.IsActive)
                return;

            var id = (PlayerId)pkt.ParticipantId;
            if (!MultiplayerSession.IsNetworkControlledPlayer(id) || MultiplayerSession.IsBodyOwnedLocally(id))
                return;

            var player = GetPlayerSafe(id);
            if (player == null || player.IsDead || player.stats == null)
                return;

            var stats = player.stats;
            try
            {
                if (SuperMeterSetter != null && Mathf.Abs(stats.SuperMeter - pkt.SuperMeter) > 0.01f)
                {
                    SuperMeterSetter.Invoke(stats, new object[] { Mathf.Clamp(pkt.SuperMeter, 0f, stats.SuperMeterMax) });
                    if (OnSuperChangedMethod != null)
                        OnSuperChangedMethod.Invoke(stats, new object[] { false });
                }

                if (HealthSetter != null && pkt.Health > stats.Health && pkt.Health <= stats.HealthMax)
                {
                    HealthSetter.Invoke(stats, new object[] { (int)pkt.Health });
                    if (OnHealthChangedMethod != null)
                        OnHealthChangedMethod.Invoke(stats, null);
                }
            }
            catch (System.Exception ex)
            {
                Plugin.LogVerbose("[Status] Owner stats not applied: " + ex.Message);
            }
        }

        static void ResetOwnedStatus()
        {
            for (int i = 0; i < 2; i++)
            {
                LastSentMeter[i] = -1f;
                LastSentHealth[i] = -1;
                NextSendAt[i] = 0f;
                NextHeartbeatAt[i] = 0f;
            }
        }

        public static bool TryBuildLocalPacket(PlayerId playerId, out PlayerStatusPacket pkt)
        {
            return TryBuildLocalPacket(GetPlayerSafe(playerId), out pkt);
        }

        public static void CaptureLocal(AbstractPlayerController player)
        {
            PlayerStatusPacket pkt;
            if (!TryBuildLocalPacket(player, out pkt))
                return;

            Apply(pkt);
        }

        public static void CaptureLocal(PlayerId playerId)
        {
            CaptureLocal(GetPlayerSafe(playerId));
        }

        public static void PushLocalStatus(AbstractPlayerController player)
        {
            PlayerStatusPacket pkt;
            if (!TryBuildLocalPacket(player, out pkt))
                return;

            Apply(pkt);
            if (Plugin.Net != null && Plugin.Net.IsConnected && MultiplayerSession.IsActive)
                Plugin.Net.SendPlayerStatus(ref pkt);
        }

        public static bool TryGetPosition(byte participantId, out Vector2 position)
        {
            position = Vector2.zero;

            if (participantId <= (byte)PlayerId.PlayerTwo)
            {
                var player = GetPlayerSafe((PlayerId)participantId);
                if (player == null)
                    return false;

                position = player.center;
                return true;
            }

            return ExtraParticipantTracker.TryGetPosition(participantId, out position);
        }

        public static bool TryGetBestDonor(byte requesterParticipantId, Vector2 requesterPos, out byte donorParticipantId, out Vector2 donorPos)
        {
            donorParticipantId = byte.MaxValue;
            donorPos = requesterPos;

            float bestDistance = float.MaxValue;
            _sortedIds.Clear();
            _sortedIds.Add((byte)PlayerId.PlayerOne);
            _sortedIds.Add((byte)PlayerId.PlayerTwo);
            foreach (var entry in _statuses)
            {
                if (_sortedIds.Contains(entry.Key))
                    continue;
                _sortedIds.Add(entry.Key);
            }

            for (int i = 0; i < _sortedIds.Count; i++)
            {
                ParticipantStatus status;
                if (!TryGet(_sortedIds[i], out status))
                    continue;
                if (!status.IsKnown || status.IsDead || !status.CanDonate)
                    continue;
                if (status.ParticipantId == requesterParticipantId)
                    continue;

                Vector2 candidatePos;
                if (!TryGetPosition(status.ParticipantId, out candidatePos))
                    continue;

                float sqrDistance = (candidatePos - requesterPos).sqrMagnitude;
                if (sqrDistance >= bestDistance)
                    continue;

                donorParticipantId = status.ParticipantId;
                donorPos = candidatePos;
                bestDistance = sqrDistance;
            }

            return donorParticipantId != byte.MaxValue;
        }

        public static bool TryGetBestReviveTarget(byte donorParticipantId, Vector2 requestPos, out byte targetParticipantId, out Vector2 targetPos)
        {
            targetParticipantId = byte.MaxValue;
            targetPos = requestPos;

            float bestDistance = float.MaxValue;
            _sortedIds.Clear();
            _sortedIds.Add((byte)PlayerId.PlayerOne);
            _sortedIds.Add((byte)PlayerId.PlayerTwo);
            foreach (var entry in _statuses)
            {
                if (_sortedIds.Contains(entry.Key))
                    continue;
                _sortedIds.Add(entry.Key);
            }

            for (int i = 0; i < _sortedIds.Count; i++)
            {
                ParticipantStatus status;
                if (!TryGet(_sortedIds[i], out status))
                    continue;
                if (!status.IsKnown || !status.IsDead)
                    continue;
                if (status.ParticipantId == donorParticipantId)
                    continue;

                Vector2 candidatePos;
                if (!TryGetPosition(status.ParticipantId, out candidatePos))
                    continue;

                float sqrDistance = (candidatePos - requestPos).sqrMagnitude;
                if (sqrDistance >= bestDistance)
                    continue;

                bestDistance = sqrDistance;
                targetParticipantId = status.ParticipantId;
                targetPos = candidatePos;
            }

            return targetParticipantId != byte.MaxValue;
        }

        public static int AppendKnownParticipants(List<byte> target, bool includeBuiltIn = false)
        {
            if (target == null)
                return 0;

            int added = 0;
            if (includeBuiltIn)
            {
                target.Add((byte)PlayerId.PlayerOne);
                target.Add((byte)PlayerId.PlayerTwo);
                added += 2;
            }

            foreach (var entry in _statuses)
            {
                if (!includeBuiltIn && entry.Key <= (byte)PlayerId.PlayerTwo)
                    continue;

                if (target.Contains(entry.Key))
                    continue;

                target.Add(entry.Key);
                added++;
            }

            return added;
        }

        public static string BuildExtraSummary()
        {
            _sortedIds.Clear();
            foreach (var entry in _statuses)
            {
                if (entry.Key <= (byte)PlayerId.PlayerTwo)
                    continue;
                _sortedIds.Add(entry.Key);
            }

            if (_sortedIds.Count == 0)
                return string.Empty;

            _sortedIds.Sort();
            var sb = new StringBuilder();
            for (int i = 0; i < _sortedIds.Count; i++)
            {
                ParticipantStatus status;
                if (!TryGet(_sortedIds[i], out status))
                    continue;

                if (sb.Length > 0)
                    sb.Append(" | ");

                sb.Append("P");
                sb.Append(status.ParticipantId + 1);
                sb.Append(" ");
                if (status.IsDead)
                {
                    sb.Append("DOWN");
                }
                else
                {
                    sb.Append("HP");
                    sb.Append(status.Health);
                    sb.Append("/");
                    sb.Append(status.HealthMax);
                }
            }

            return sb.ToString();
        }

        static string GetCharacterName(AbstractPlayerController player)
        {
            if (player == null || player.stats == null)
                return "Cuphead";
            if (player.stats.isChalice)
                return "Ms. Chalice";

            bool playerOneIsMugman = PlayerManager.player1IsMugman;
            if (player.id == PlayerId.PlayerOne)
                return playerOneIsMugman ? "Mugman" : "Cuphead";
            if (player.id == PlayerId.PlayerTwo)
                return playerOneIsMugman ? "Cuphead" : "Mugman";
            return "Cuphead";
        }

        static AbstractPlayerController GetPlayerSafe(PlayerId playerId)
        {
            try
            {
                return PlayerManager.GetPlayer(playerId);
            }
            catch
            {
                return null;
            }
        }
    }
}
