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
            if (!ShouldAccept(pkt.PlayerId))
                return;

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
            PreparePendingForStats(id);
            ApplyPreparedStats(stats, id);
        }

        public static void PreparePendingForStats(PlayerId id)
        {
            if (!_pending.HasValue) return;
            var pkt = _pending.Value;
            if (pkt.PlayerId != (byte)id) return;

            ApplyToPlayerData(id, pkt);
        }

        public static void ApplyPreparedStats(PlayerStatsManager stats, PlayerId id)
        {
            if (!_pending.HasValue) return;
            var pkt = _pending.Value;
            if (pkt.PlayerId != (byte)id) return;

            ApplyToPlayerData(id, pkt);
            ApplyToStats(stats, id, pkt);
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
                DecodedLoadout decoded = Decode(pkt, chaliceBlocked: false);
                loadout.primaryWeapon = decoded.PrimaryWeapon;
                loadout.secondaryWeapon = decoded.SecondaryWeapon;
                loadout.super = decoded.Super;
                loadout.charm = decoded.Charm;
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

            ApplyToStats(player.stats, id, pkt);
        }

        static void ApplyToStats(PlayerStatsManager stats, PlayerId id, LobbySyncPacket pkt)
        {
            if (stats == null)
                return;

            var loadout = stats.Loadout;
            if (loadout == null)
                return;

            DecodedLoadout decoded = Decode(pkt, IsChaliceBlocked(id));
            loadout.primaryWeapon = decoded.PrimaryWeapon;
            loadout.secondaryWeapon = decoded.SecondaryWeapon;
            loadout.super = decoded.Super;
            loadout.charm = decoded.Charm;
            Traverse.Create(stats).Property("Loadout").SetValue(loadout);

            try { stats.isChalice = decoded.IsChalice; }
            catch { }
        }

        struct DecodedLoadout
        {
            public Weapon PrimaryWeapon;
            public Weapon SecondaryWeapon;
            public Super Super;
            public Charm Charm;
            public bool IsChalice;
        }

        static DecodedLoadout Decode(LobbySyncPacket pkt, bool chaliceBlocked)
        {
            bool dlcAvailable = IsDlcAvailable();
            var charm = SanitizeCharm(LoadoutCodec.DecodeCharm(pkt.Charm), dlcAvailable);
            bool isChalice = !chaliceBlocked
                && dlcAvailable
                && pkt.IsChalice != 0
                && charm == Charm.charm_chalice;

            return new DecodedLoadout
            {
                PrimaryWeapon = SanitizeWeapon(LoadoutCodec.DecodeWeapon(pkt.Weapon1, primarySlot: true), primarySlot: true, dlcAvailable: dlcAvailable),
                SecondaryWeapon = SanitizeWeapon(LoadoutCodec.DecodeWeapon(pkt.Weapon2, primarySlot: false), primarySlot: false, dlcAvailable: dlcAvailable),
                Super = NormalizeSuperForCharacter(SanitizeSuper(LoadoutCodec.DecodeSuper(pkt.Super), dlcAvailable), isChalice),
                Charm = charm,
                IsChalice = isChalice,
            };
        }

        static Weapon SanitizeWeapon(Weapon weapon, bool primarySlot, bool dlcAvailable)
        {
            if (!dlcAvailable && IsDlcWeapon(weapon))
                return primarySlot ? Weapon.level_weapon_peashot : Weapon.None;

            return LoadoutCodec.NormalizeWeapon(weapon, primarySlot);
        }

        static Super SanitizeSuper(Super super, bool dlcAvailable)
        {
            if (!dlcAvailable && IsDlcSuper(super))
                return Super.None;

            return super;
        }

        static Charm SanitizeCharm(Charm charm, bool dlcAvailable)
        {
            if (!dlcAvailable && IsDlcCharm(charm))
                return Charm.None;

            return charm;
        }

        static Super NormalizeSuperForCharacter(Super super, bool isChalice)
        {
            if (isChalice)
            {
                if (super == Super.level_super_beam) return Super.level_super_chalice_vert_beam;
                if (super == Super.level_super_invincible) return Super.level_super_chalice_shield;
                if (super == Super.level_super_ghost) return Super.level_super_chalice_iii;
                if (super == Super.plane_super_bomb) return Super.plane_super_chalice_bomb;
                return super;
            }

            if (super == Super.level_super_chalice_vert_beam) return Super.level_super_beam;
            if (super == Super.level_super_chalice_shield) return Super.level_super_invincible;
            if (super == Super.level_super_chalice_iii) return Super.level_super_ghost;
            if (super == Super.level_super_chalice_bounce) return Super.None;
            if (super == Super.plane_super_chalice_bomb) return Super.plane_super_bomb;
            return super;
        }

        static bool IsDlcWeapon(Weapon weapon)
        {
            return weapon == Weapon.level_weapon_wide_shot
                || weapon == Weapon.level_weapon_upshot
                || weapon == Weapon.level_weapon_crackshot
                || weapon == Weapon.level_weapon_splitter;
        }

        static bool IsDlcSuper(Super super)
        {
            return super == Super.level_super_chalice_iii
                || super == Super.level_super_chalice_vert_beam
                || super == Super.level_super_chalice_shield
                || super == Super.level_super_chalice_bounce
                || super == Super.plane_super_chalice_bomb;
        }

        static bool IsDlcCharm(Charm charm)
        {
            return charm == Charm.charm_chalice
                || charm == Charm.charm_healer
                || charm == Charm.charm_curse;
        }

        static bool IsDlcAvailable()
        {
            try { return DLCManager.DLCEnabled(); }
            catch { return false; }
        }

        static bool IsChaliceBlocked(PlayerId id)
        {
            try
            {
                var blockedSlots = Level.Current != null ? Level.Current.BlockChaliceCharm : null;
                int index = (int)id;
                return blockedSlots != null
                    && index >= 0
                    && index < blockedSlots.Length
                    && blockedSlots[index];
            }
            catch
            {
                return false;
            }
        }

        static bool ShouldAccept(byte playerId)
        {
            if (!MultiplayerSession.IsActive)
                return true;

            if (playerId > (byte)PlayerId.PlayerTwo)
                return false;

            return MultiplayerSession.IsNetworkControlledPlayer((PlayerId)playerId);
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
