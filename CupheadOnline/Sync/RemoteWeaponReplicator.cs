using System.Collections.Generic;
using UnityEngine;
using CupheadOnline.Net;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Applies weapon events received from the network onto the remote player's
    /// weapon manager, so the correct visual / audio effects play locally.
    ///
    /// We intentionally do NOT spawn actual projectiles on the client for the remote
    /// player — the host's projectiles are replicated via EnemySyncManager's enemy-state
    /// logic, and the visual muzzle flash / animation is driven here.
    ///
    /// Rationale: spawning physics-enabled projectiles on both sides would double-count
    /// damage. The host is the only source of truth for collision.
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

            // Set look direction so the muzzle flash appears in the right direction
            if (player.motor != null)
            {
                var t = HarmonyLib.Traverse.Create(player.motor);
                var dir = new Trilean2(pkt.AimX, pkt.AimY);
                t.Property("LookDirection").SetValue(dir);
                t.Property("TrueLookDirection").SetValue(dir);
            }

            var wm = player.weaponManager;
            if (wm == null) return;

            switch ((WeaponEventType)pkt.EventType)
            {
                case WeaponEventType.Fire:
                    // Raise the fire anim + a visual-only tracer so the shot is readable
                    TriggerAnimatorParam(player, "Shooting", true);
                    SpawnTracer(player, pkt);
                    break;

                case WeaponEventType.Ex:
                    TriggerAnimatorTrigger(player, "Ex");
                    SpawnTracer(player, pkt);
                    break;

                case WeaponEventType.Super:
                    TriggerAnimatorTrigger(player, "Super");
                    break;

                case WeaponEventType.Parry:
                    TriggerAnimatorTrigger(player, "Parry");
                    break;

                case WeaponEventType.Switch:
                    ApplyWeaponSwitch(wm, pkt.WeaponId);
                    break;
            }
        }

        static void SpawnTracer(LevelPlayerController player, WeaponEventPacket pkt)
        {
            // Only where real projectiles are missing: the proxy slot on the
            // client. The host simulates the guest's weapons for real.
            if (MultiplayerSession.IsHost)
                return;

            Vector2 origin;
            try { origin = player.center; }
            catch { origin = player.transform.position; }

            RemoteShotTracer.Spawn(origin, new Vector2(pkt.AimX, pkt.AimY));
        }

        static void TriggerAnimatorParam(LevelPlayerController player, string param, bool value)
        {
            var anim = player.animationController?.animator;
            if (anim == null) return;
            anim.SetBool(param, value);
        }

        static void TriggerAnimatorTrigger(LevelPlayerController player, string trigger)
        {
            var anim = player.animationController?.animator;
            if (anim == null) return;
            anim.SetTrigger(trigger);
        }

        static void ApplyWeaponSwitch(LevelPlayerWeaponManager wm, byte weaponId)
        {
            // The weapon enum value was sent — try to switch to that weapon
            try
            {
                var weapon = LoadoutCodec.DecodeWeapon(weaponId, primarySlot: true);
                // Call SwitchWeapon via reflection since it's likely private
                var mi = typeof(LevelPlayerWeaponManager).GetMethod(
                    "SwitchWeapon",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                mi?.Invoke(wm, new object[] { weapon });
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
