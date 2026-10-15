using System.Collections.Generic;
using UnityEngine;
using CupheadOnline.Net;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Applies weapon events received from the network.
    ///
    /// The other built-in player (P1 on the guest, P2 on the host) runs its real
    /// LevelPlayerWeaponManager from the routed remote input, so its shots, EX,
    /// super and parry animations already play natively — layering animator
    /// triggers or tracer streaks on top only doubled them. For built-in slots
    /// we therefore only re-assert the equipped weapon (in case a switch press
    /// was lost). Extra participants' visual-only avatars get the full treatment
    /// in ExtraRemoteAvatarManager.
    /// </summary>
    public static class RemoteWeaponReplicator
    {
        static readonly Dictionary<byte, uint> _lastEventTicks =
            new Dictionary<byte, uint>(4);

        static RemoteWeaponReplicator()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static void Apply(WeaponEventPacket pkt)
        {
            if (!ShouldApplyEvent(pkt.PlayerId, pkt.Tick))
                return;

            if (pkt.PlayerId > (byte)PlayerId.PlayerTwo)
            {
                ExtraRemoteAvatarManager.ApplyWeaponEvent(pkt);
                return;
            }

            if (MultiplayerSession.IsHost
             && MultiplayerSession.IsNetworkControlledPlayer((PlayerId)pkt.PlayerId))
            {
                return;
            }

            LevelPlayerController player;
            try
            {
                player = PlayerManager.GetPlayer((PlayerId)pkt.PlayerId) as LevelPlayerController;
            }
            catch
            {
                return;
            }

            if (player == null) return;

            var wm = player.weaponManager;
            if (wm == null) return;

            if ((WeaponEventType)pkt.EventType == WeaponEventType.Switch)
                ApplyWeaponSwitch(wm, pkt.WeaponId);
        }

        static readonly System.Reflection.MethodInfo SwitchWeaponMethod =
            HarmonyLib.AccessTools.Method(typeof(LevelPlayerWeaponManager), "SwitchWeapon", new[] { typeof(Weapon) });
        static readonly HarmonyLib.AccessTools.FieldRef<LevelPlayerWeaponManager, Weapon> CurrentWeaponRef =
            CupheadOnline.Patches.SafeAccess.FieldRef<LevelPlayerWeaponManager, Weapon>("currentWeapon");

        static void ApplyWeaponSwitch(LevelPlayerWeaponManager wm, byte weaponId)
        {
            try
            {
                var weapon = LoadoutCodec.DecodeWeapon(weaponId, primarySlot: true);
                if (weapon == Weapon.None || (CurrentWeaponRef != null && CurrentWeaponRef(wm) == weapon))
                    return; // the native switch press already got there

                SwitchWeaponMethod?.Invoke(wm, new object[] { weapon });
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[WeaponReplicator] Weapon switch failed: {ex.Message}");
            }
        }

        static bool ShouldApplyEvent(byte playerId, uint tick)
        {
            uint previous;
            if (_lastEventTicks.TryGetValue(playerId, out previous))
            {
                if (NetTick.IsOlder(tick, previous))
                    return false;
                if (NetTick.IsNewer(tick, previous))
                    _lastEventTicks[playerId] = tick;
                return true;
            }

            _lastEventTicks[playerId] = tick;
            return true;
        }

        static void Reset()
        {
            _lastEventTicks.Clear();
        }
    }
}
