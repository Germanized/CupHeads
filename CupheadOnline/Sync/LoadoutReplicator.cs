using CupheadOnline.Net;
using HarmonyLib;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Applies the remote player's loadout (weapons / super / charm / character)
    /// so the correct sprites, animations, and mechanics load for their avatar.
    ///
    /// Flow:
    ///   1. During the lobby both sides call SendLobbySync with their local loadout.
    ///   2. PacketDispatcher calls Apply() with the received packet.
    ///   3. We store it in _pending; the PlayerManagerPatch (StatsLevelInitPatch)
    ///      reads it when the remote player's stats are initialised.
    /// </summary>
    public static class LoadoutReplicator
    {
        private static LobbySyncPacket? _pending;
        private static LobbySyncPacket? _lastSent;
        private static LobbySyncPacket? _lastReceived;
        private static float _nextBroadcastAt;

        static LoadoutReplicator()
        {
            MultiplayerSession.OnSessionStarted += Reset;
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static void Apply(LobbySyncPacket pkt)
        {
            _pending = pkt;
            ApplyToPlayerData((PlayerId)pkt.PlayerId, pkt);
            if (ShouldApplyLiveNow())
                ApplyToLivePlayer((PlayerId)pkt.PlayerId, pkt);

            if (!_lastReceived.HasValue || !PacketsEqual(_lastReceived.Value, pkt))
            {
                Plugin.Log.LogInfo(
                    $"[Loadout] Received remote loadout for Player {pkt.PlayerId}: " +
                    $"W1={pkt.Weapon1} W2={pkt.Weapon2} Super={pkt.Super} Charm={pkt.Charm} Chalice={pkt.IsChalice}");
                _lastReceived = pkt;
            }
        }

        public static void ApplyPending(PlayerStatsManager stats, PlayerId id)
        {
            if (!_pending.HasValue) return;
            var pkt = _pending.Value;
            if (pkt.PlayerId != (byte)id) return;

            ApplyToPlayerData(id, pkt);
            ApplyToStats(stats, pkt);
            _pending = null;
        }

        /// <summary>
        /// Keeps the active save/loadout mirrored while connected so internal menus
        /// start from the same state on both peers.
        /// </summary>
        public static void Update()
        {
            if (!MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;
            if (!ShouldBroadcastNow())
                return;

            LobbySyncPacket pkt;
            if (!TryBuildLocalPacket(out pkt))
                return;

            bool changed = !_lastSent.HasValue || !PacketsEqual(_lastSent.Value, pkt);
            if (!changed && Time.unscaledTime < _nextBroadcastAt)
                return;

            Plugin.Net.SendLobbySync(ref pkt);
            _lastSent = pkt;
            _nextBroadcastAt = Time.unscaledTime + (changed ? 0.2f : 1.5f);
        }

        public static void BroadcastLocalLoadout()
        {
            if (!MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;
            if (!ShouldBroadcastNow())
                return;

            LobbySyncPacket pkt;
            if (!TryBuildLocalPacket(out pkt))
                return;

            Plugin.Net.SendLobbySync(ref pkt);
            _lastSent = pkt;
            _nextBroadcastAt = Time.unscaledTime + 1.5f;
        }

        static bool TryBuildLocalPacket(out LobbySyncPacket pkt)
        {
            pkt = default(LobbySyncPacket);
            if (PlayerData.Data == null || PlayerData.Data.Loadouts == null)
                return false;

            var loadout = PlayerData.Data.Loadouts.GetPlayerLoadout(MultiplayerSession.LocalId);
            bool isChalice = false;

            var player = MultiplayerSession.GetLocalController();
            if (player != null && player.stats != null)
                isChalice = player.stats.isChalice;
            else
                isChalice = loadout.charm == Charm.charm_chalice;

            pkt = new LobbySyncPacket
            {
                PlayerId  = (byte)MultiplayerSession.LocalId,
                Weapon1   = LoadoutCodec.EncodeWeapon(loadout.primaryWeapon),
                Weapon2   = LoadoutCodec.EncodeWeapon(loadout.secondaryWeapon),
                Super     = LoadoutCodec.EncodeSuper(loadout.super),
                Charm     = LoadoutCodec.EncodeCharm(loadout.charm),
                IsChalice = (byte)(isChalice ? 1 : 0),
            };
            return true;
        }

        static void ApplyToPlayerData(PlayerId id, LobbySyncPacket pkt)
        {
            try
            {
                if (PlayerData.Data == null || PlayerData.Data.Loadouts == null)
                    return;

                var loadout = PlayerData.Data.Loadouts.GetPlayerLoadout(id);
                loadout.primaryWeapon = LoadoutCodec.DecodeWeapon(pkt.Weapon1, primarySlot: true);
                loadout.secondaryWeapon = LoadoutCodec.DecodeWeapon(pkt.Weapon2, primarySlot: false);
                loadout.super = LoadoutCodec.DecodeSuper(pkt.Super);
                loadout.charm = LoadoutCodec.DecodeCharm(pkt.Charm);
            }
            catch
            {
            }
        }

        static void ApplyToLivePlayer(PlayerId id, LobbySyncPacket pkt)
        {
            AbstractPlayerController player;
            try
            {
                player = PlayerManager.GetPlayer(id);
            }
            catch
            {
                return;
            }

            if (player == null || player.stats == null)
                return;

            ApplyToStats(player.stats, pkt);
        }

        static void ApplyToStats(PlayerStatsManager stats, LobbySyncPacket pkt)
        {
            if (stats == null)
                return;

            var loadout = stats.Loadout;
            loadout.primaryWeapon = LoadoutCodec.DecodeWeapon(pkt.Weapon1, primarySlot: true);
            loadout.secondaryWeapon = LoadoutCodec.DecodeWeapon(pkt.Weapon2, primarySlot: false);
            loadout.super = LoadoutCodec.DecodeSuper(pkt.Super);
            loadout.charm = LoadoutCodec.DecodeCharm(pkt.Charm);
            Traverse.Create(stats).Property("Loadout").SetValue(loadout);

            try { stats.isChalice = pkt.IsChalice != 0; }
            catch { }
        }

        static bool ShouldBroadcastNow()
        {
            return Level.Current == null;
        }

        static bool ShouldApplyLiveNow()
        {
            return Level.Current == null;
        }

        static bool PacketsEqual(LobbySyncPacket left, LobbySyncPacket right)
        {
            return left.PlayerId == right.PlayerId
                && left.Weapon1 == right.Weapon1
                && left.Weapon2 == right.Weapon2
                && left.Super == right.Super
                && left.Charm == right.Charm
                && left.IsChalice == right.IsChalice;
        }

        static void Reset()
        {
            _pending = null;
            _lastSent = null;
            _lastReceived = null;
            _nextBroadcastAt = 0f;
        }
    }
}
