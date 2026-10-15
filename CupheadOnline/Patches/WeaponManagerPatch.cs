using HarmonyLib;
using CupheadOnline.Net;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Hooks into LevelPlayerWeaponManager to broadcast weapon events.
    /// We subscribe to the existing C# events rather than patching private methods,
    /// which is more stable and avoids method-name fragility.
    ///
    /// Subscription happens in the PlayerManagerPatch (LevelInit postfix) so we
    /// have a valid reference to the weaponManager at the right lifecycle point.
    /// </summary>
    public static class WeaponManagerPatch
    {
        // Called by PlayerManagerPatch after player construction
        public static void Subscribe(LevelPlayerController player)
        {
            if (player == null) return;
            var wm = player.weaponManager;
            if (wm == null) return;

            // Only subscribe for our LOCAL player — we broadcast their actions
            if (!MultiplayerSession.IsLocalPlayer(player.id)) return;

            wm.OnWeaponFire      += () => BroadcastShot(player, (byte)WeaponEventType.Fire);
            wm.OnExStart         += () => BroadcastShot(player, (byte)WeaponEventType.Ex);
            wm.OnSuperStart      += () => BroadcastShot(player, (byte)WeaponEventType.Super);
            wm.OnWeaponChangeEvent += (next) => BroadcastWeaponSwitch(player, next);
        }

        /// <summary>
        /// Shot / EX / super / parry events only drive the visual-only avatars of
        /// extra (3rd+) participants: the built-in partner slot replays these
        /// natively from routed input. Skipping them in plain two-player
        /// sessions keeps a reliable packet per bullet off the wire.
        /// </summary>
        internal static bool ShouldSendVisualEvents()
        {
            if (Plugin.Net == null)
                return false;
            if (MultiplayerSession.IsHost)
                return Plugin.Net.ConnectedPeerCount > 1;
            return ExtraParticipantTracker.TotalCount > 0;
        }

        static void BroadcastShot(LevelPlayerController player, byte eventType)
        {
            if (!MultiplayerSession.IsActive) return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected) return;
            if (!ShouldSendVisualEvents()) return;
            var motor = player.motor;
            var pkt = new WeaponEventPacket
            {
                PlayerId  = (byte)player.id,
                EventType = eventType,
                AimX      = (sbyte)(motor?.LookDirection.x.Value ?? 1),
                AimY      = (sbyte)(motor?.LookDirection.y.Value ?? 0),
                WeaponId  = 0,
                Tick      = MultiplayerSession.Tick,
            };
            Plugin.Net.SendWeaponEvent(ref pkt);
        }

        static void BroadcastWeaponSwitch(LevelPlayerController player, Weapon next)
        {
            if (!MultiplayerSession.IsActive) return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected) return;
            var motor = player.motor;
            var pkt = new WeaponEventPacket
            {
                PlayerId  = (byte)player.id,
                EventType = (byte)WeaponEventType.Switch,
                AimX      = (sbyte)(motor?.LookDirection.x.Value ?? 1),
                AimY      = 0,
                WeaponId  = LoadoutCodec.EncodeWeapon(next),
                Tick      = MultiplayerSession.Tick,
            };
            Plugin.Net.SendWeaponEvent(ref pkt);
        }
    }

    /// <summary>
    /// Patch the parry controller to broadcast parry events.
    /// StartParry is the game's entry point when a parry begins; there is no
    /// method named "Parry" on LevelPlayerParryController.
    /// </summary>
    [HarmonyPatch(typeof(LevelPlayerParryController), "StartParry")]
    public static class ParryPatch
    {
        static void Postfix(LevelPlayerParryController __instance)
        {
            if (!MultiplayerSession.IsActive) return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected) return;
            if (!WeaponManagerPatch.ShouldSendVisualEvents()) return;
            var player = __instance.player;
            if (player == null || !MultiplayerSession.IsLocalPlayer(player.id)) return;

            var motor = player.motor;
            var pkt = new WeaponEventPacket
            {
                PlayerId  = (byte)player.id,
                EventType = (byte)WeaponEventType.Parry,
                AimX      = (sbyte)(motor?.LookDirection.x.Value ?? 1),
                AimY      = (sbyte)(motor?.LookDirection.y.Value ?? 0),
                WeaponId  = 0,
                Tick      = MultiplayerSession.Tick,
            };
            Plugin.Net.SendWeaponEvent(ref pkt);
        }
    }
}
