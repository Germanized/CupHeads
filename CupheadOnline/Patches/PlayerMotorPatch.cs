using HarmonyLib;
using UnityEngine;
using CupheadOnline.Net;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Patches LevelPlayerMotor.FixedUpdate.
    ///
    /// Host: both built-in gameplay slots run the real Cuphead motor so the host
    /// remains authoritative for movement, collisions, jump state, death, and
    /// animation transitions.
    ///
    /// Client: the host-controlled slot stays a proxy that consumes snapshots,
    /// and the guest's own body is predicted locally and reconciled against
    /// the host's acknowledged state (OwnPlayerReconciler).
    /// </summary>
    [HarmonyPatch(typeof(LevelPlayerMotor), "FixedUpdate")]
    public static class PlayerMotorPatch
    {
        static bool Prefix(LevelPlayerMotor __instance)
        {
            if (!MultiplayerSession.IsActive)
                return true;

            var player = __instance.player;
            if (player == null)
                return true;

            byte extraParticipantId;
            if (ExtraRemoteAvatarManager.TryGetAvatarParticipantId(__instance, out extraParticipantId))
            {
                RemoteInputDriver.Tick(extraParticipantId);
                ApplyRemoteState(__instance, extraParticipantId);
                return false;
            }

            if (MultiplayerSession.IsNetworkControlledPlayer(player.id))
            {
                RemoteInputDriver.Tick(player.id);

                // The host simulates the guest with the real gameplay motor.
                if (MultiplayerSession.IsHost && player.id <= PlayerId.PlayerTwo)
                    return true;

                ApplyRemoteState(__instance, (byte)player.id);
                return false;
            }

            return true;
        }

        static void Postfix(LevelPlayerMotor __instance)
        {
            if (!MultiplayerSession.IsActive)
                return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            var player = __instance.player;
            if (player == null)
                return;

            bool authoritativeBuiltIn = MultiplayerSession.IsHost && player.id <= PlayerId.PlayerTwo;
            if (!authoritativeBuiltIn && !MultiplayerSession.IsLocalPlayer(player.id))
                return;

            var pkt = BuildStatePacket(player, __instance);

            if (authoritativeBuiltIn)
            {
                // For the guest's body, tell the guest which of its inputs this
                // state reflects so it can reconcile against its own history.
                if (MultiplayerSession.IsNetworkControlledPlayer(player.id))
                    RemoteInputDriver.TryGetInputAck((byte)player.id, out pkt.AckInputSeq, out pkt.AckAgeMs);

                Plugin.Net.SendPlayerState(ref pkt);
                return;
            }

            Plugin.Net.SendPlayerState(ref pkt);
            if (MultiplayerSession.IsClient)
            {
                Vector2 step = OwnPlayerReconciler.Apply(__instance.transform);
                if (step != Vector2.zero && LastPositionFixedRef != null && LastPositionRef != null)
                {
                    // The motor derives MoveDirection (and so the run/idle pose)
                    // from position deltas; shift its reference points with the
                    // nudge so a correction never reads as player movement.
                    LastPositionFixedRef(__instance) += step;
                    LastPositionRef(__instance) += step;
                }
                OwnPlayerReconciler.RecordLocalPosition(__instance.transform.position);
            }
        }

        static readonly AccessTools.FieldRef<LevelPlayerMotor, Vector2> LastPositionFixedRef =
            SafeAccess.FieldRef<LevelPlayerMotor, Vector2>("lastPositionFixed");
        static readonly AccessTools.FieldRef<LevelPlayerMotor, Vector2> LastPositionRef =
            SafeAccess.FieldRef<LevelPlayerMotor, Vector2>("lastPosition");

        internal static byte BuildFlags(AbstractPlayerController player, LevelPlayerMotor m)
        {
            byte f = 0;
            if (m.Grounded) f |= 1;
            if (m.Dashing) f |= 2;
            if (m.Ducking) f |= 4;
            if (m.GravityReversed) f |= 8;
            if (m.IsHit) f |= 16;
            if (m.IsUsingSuperOrEx) f |= 32;
            if (player != null && player.IsDead) f |= 64;
            return f;
        }

        internal static PlayerStatePacket BuildStatePacket(LevelPlayerController player, LevelPlayerMotor motor)
        {
            return new PlayerStatePacket
            {
                PlayerId = (byte)player.id,
                PosX = motor.transform.position.x,
                PosY = motor.transform.position.y,
                LookX = (sbyte)motor.LookDirection.x.Value,
                LookY = (sbyte)motor.LookDirection.y.Value,
                Flags = BuildFlags(player, motor),
                AnimState = GetAnimHash(player),
                Tick = MultiplayerSession.Tick,
            };
        }

        internal static byte GetAnimHash(LevelPlayerController player)
        {
            var anim = player.animationController?.animator;
            if (anim == null)
                return 0;

            return (byte)(anim.GetCurrentAnimatorStateInfo(0).fullPathHash & 0xFF);
        }

        static void ApplyRemoteState(LevelPlayerMotor motor, byte participantId)
        {
            var snapshot = RemotePlayer.GetNextSnapshot(participantId);
            if (!snapshot.HasValue)
                return;

            var s = snapshot.Value;
            var target = new Vector3(s.PosX, s.PosY, motor.transform.position.z);
            motor.transform.position = Vector3.Lerp(
                motor.transform.position,
                target,
                Mathf.Min(1f, 20f * Time.fixedDeltaTime));

            var t = Traverse.Create(motor);
            t.Property("LookDirection").SetValue(new Trilean2(s.LookX, s.LookY));
            t.Property("TrueLookDirection").SetValue(new Trilean2(s.LookX, s.LookY));

            InputFramePacket input;
            if (RemoteInputDriver.TryGetCurrent(participantId, out input))
            {
                t.Property("MoveDirection").SetValue(new Trilean2(
                    QuantizeAxis(input.AxisX, input.AxisY, horizontal: true),
                    QuantizeAxis(input.AxisX, input.AxisY, horizontal: false)));
            }
            else
            {
                t.Property("MoveDirection").SetValue(new Trilean2(0, 0));
            }

            // Only Grounded / Locked / GravityReversed have setters. Dashing,
            // Ducking, IsHit, and IsUsingSuperOrEx are computed getters backed by
            // the motor's internal managers; their visual effect on the proxy is
            // driven through the flag-transition events below instead.
            t.Property("Grounded").SetValue(s.Grounded);
            t.Property("Locked").SetValue(false);
            t.Property("GravityReversed").SetValue(s.GravReversed);

            RemotePlayer.UpdateStateTransitions(participantId, motor, s);
        }

        /// <summary>
        /// Same 8-way quantisation as PlayerInput.GetAxisInt: a 0.375 radial dead
        /// zone, then the normalised component against sin(22.5°).
        /// </summary>
        internal static int QuantizeAxis(float x, float y, bool horizontal)
        {
            float magnitude = Mathf.Sqrt(x * x + y * y);
            if (magnitude < 0.375f)
                return 0;

            float component = (horizontal ? x : y) / magnitude;
            if (component > 0.38268f) return 1;
            if (component < -0.38268f) return -1;
            return 0;
        }
    }

    // NOTE: PlayerInput.GetAxis / GetAxisInt / GetButton are intentionally not
    // patched. They all read PlayerInput.actions (the slot's Rewired.Player),
    // and the Rewired postfixes in InputRouterPatches already substitute the
    // remote input there, so the game's own quantisation (dead zone, 8-way
    // snapping, duck threshold, camera rotation) runs unchanged for the remote
    // player exactly as it does on that player's own machine.
}
