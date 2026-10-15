using HarmonyLib;
using CupheadOnline.Net;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Hybrid co-op damage.
    ///
    /// The host still owns scenes, saves, boss state, RNG, and progression. Damage
    /// to a player body is owned by that player's machine when latency-friendly
    /// damage is enabled, so a guest does not lose HP because the host saw an old
    /// lagged position.
    ///
    /// Only hits that actually land are replicated. PlayerDamageReceiver.TakeDamage
    /// is called every frame a hazard overlaps the player, including during the
    /// post-hit invulnerability window, while super-invincible, and while the
    /// player cannot take damage. Replicating those calls flooded the reliable
    /// channel and let the peer's proxy (whose invulnerability state is not
    /// simulated) take hits its owner never took.
    /// </summary>
    [HarmonyPatch(typeof(PlayerDamageReceiver), nameof(PlayerDamageReceiver.TakeDamage))]
    public static class PlayerDamagePatch
    {
        static readonly AccessTools.FieldRef<PlayerDamageReceiver, float> InvulnerabilityTimer =
            SafeAccess.FieldRef<PlayerDamageReceiver, float>("timer");

        public struct HitSnapshot
        {
            public bool Valid;
            public int Health;
            public float Timer;
            public bool SuperInvincible;
        }

        static bool Prefix(PlayerDamageReceiver __instance, DamageDealer.DamageInfo info, out HitSnapshot __state)
        {
            __state = default(HitSnapshot);
            if (!MultiplayerSession.IsActive)
                return true;
            if (LocalDevSession.IsActive)
                return true;

            __state = Capture(__instance);

            var player = __instance.GetComponent<AbstractPlayerController>();
            if (player == null)
                return true;

            if (!Plugin.LatencyFriendlyDamage)
            {
                return MultiplayerSession.IsHost || DamageAuthority.IsAuthorised(__instance, info);
            }

            if (MultiplayerSession.IsClient)
            {
                if (MultiplayerSession.IsLocalPlayer(player.id))
                    return true;

                return DamageAuthority.IsAuthorised(__instance, info);
            }

            if (MultiplayerSession.IsHost && MultiplayerSession.IsNetworkControlledPlayer(player.id))
                return DamageAuthority.IsAuthorised(__instance, info);

            return true;
        }

        static void Postfix(PlayerDamageReceiver __instance, DamageDealer.DamageInfo info, HitSnapshot __state)
        {
            if (!MultiplayerSession.IsActive)
                return;
            if (LocalDevSession.IsActive)
                return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected)
                return;
            if (info.damage <= 0f && info.stoneTime <= 0f)
                return;
            if (DamageAuthority.IsApplyingAuthorizedDamage)
                return;
            if (!Landed(__instance, info, __state))
                return;

            var player = __instance.GetComponent<AbstractPlayerController>();
            if (player == null)
                return;

            if (!Plugin.LatencyFriendlyDamage)
            {
                if (!MultiplayerSession.IsHost)
                    return;

                Plugin.Net.SendDamageEventForParticipant(
                    (byte)player.id,
                    info.damage,
                    info.stoneTime,
                    (byte)info.damageSource,
                    MultiplayerSession.Tick);
                return;
            }

            if (MultiplayerSession.IsHost)
            {
                if (MultiplayerSession.IsNetworkControlledPlayer(player.id))
                    return;

                Plugin.Net.SendDamageEventForParticipant(
                    (byte)player.id,
                    info.damage,
                    info.stoneTime,
                    (byte)info.damageSource,
                    MultiplayerSession.Tick);
                return;
            }

            if (MultiplayerSession.IsClient && MultiplayerSession.IsLocalPlayer(player.id))
            {
                var pkt = new DamageEventPacket
                {
                    TargetPlayerId = (byte)player.id,
                    Damage = info.damage,
                    StoneTime = info.stoneTime,
                    Source = (byte)info.damageSource,
                    Tick = MultiplayerSession.Tick,
                };
                Plugin.Net.SendDamageEvent(ref pkt);
            }
        }

        static HitSnapshot Capture(PlayerDamageReceiver receiver)
        {
            var snapshot = new HitSnapshot();
            try
            {
                var player = receiver != null ? receiver.GetComponent<AbstractPlayerController>() : null;
                if (player == null || player.stats == null)
                    return snapshot;

                snapshot.Health = player.stats.Health;
                snapshot.SuperInvincible = player.stats.SuperInvincible;
                snapshot.Timer = InvulnerabilityTimer != null ? InvulnerabilityTimer(receiver) : 0f;
                snapshot.Valid = true;
            }
            catch
            {
                snapshot.Valid = false;
            }

            return snapshot;
        }

        /// <summary>
        /// True when the call got past every early-out in TakeDamage. A landed
        /// hit always starts the 2 s invulnerability timer from zero and usually
        /// costs health (shields and hearts may absorb the health part).
        /// </summary>
        static bool Landed(PlayerDamageReceiver receiver, DamageDealer.DamageInfo info, HitSnapshot before)
        {
            if (!before.Valid)
                return true;

            if (info.damage <= 0f)
                return !before.SuperInvincible;

            var after = Capture(receiver);
            if (!after.Valid)
                return true;

            if (after.Health < before.Health)
                return true;

            return before.Timer <= 0f && after.Timer > 0f;
        }
    }
}
