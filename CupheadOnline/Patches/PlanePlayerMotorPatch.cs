using HarmonyLib;
using UnityEngine;
using CupheadOnline.Net;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Plane (shmup) level movement sync — the same proxy model as
    /// LevelPlayerMotor: the host runs the real motor for both built-in slots,
    /// the client's host-controlled slot consumes snapshots, and the client's own
    /// plane sends state for host-side correction.
    /// </summary>
    [HarmonyPatch(typeof(PlanePlayerMotor), "FixedUpdate")]
    public static class PlanePlayerMotorPatch
    {
        static bool Prefix(PlanePlayerMotor __instance)
        {
            if (!MultiplayerSession.IsActive)
                return true;

            var player = __instance != null ? __instance.player : null;
            if (player == null)
                return true;

            if (!MultiplayerSession.IsNetworkControlledPlayer(player.id))
            {
                if (MultiplayerSession.IsLocalPlayer(player.id))
                    MultiplayerSession.IncrementTick();
                return true;
            }

            RemoteInputDriver.Tick(player.id);

            // The host simulates the guest's plane with the real motor.
            if (MultiplayerSession.IsHost && player.id <= PlayerId.PlayerTwo)
                return true;

            ApplyRemoteState(__instance, (byte)player.id);
            return false;
        }

        static void Postfix(PlanePlayerMotor __instance)
        {
            if (!MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            var player = __instance != null ? __instance.player : null;
            if (player == null)
                return;

            bool authoritativeBuiltIn = MultiplayerSession.IsHost && player.id <= PlayerId.PlayerTwo;
            if (!authoritativeBuiltIn && !MultiplayerSession.IsLocalPlayer(player.id))
                return;

            var pkt = new PlayerStatePacket
            {
                PlayerId = (byte)player.id,
                PosX = __instance.transform.position.x,
                PosY = __instance.transform.position.y,
                LookX = (sbyte)__instance.MoveDirection.x.Value,
                LookY = (sbyte)__instance.MoveDirection.y.Value,
                Flags = (byte)(player.IsDead ? 64 : 0),
                AnimState = 0,
                Tick = MultiplayerSession.Tick,
            };
            Plugin.Net.SendPlayerState(ref pkt);
        }

        static void ApplyRemoteState(PlanePlayerMotor motor, byte participantId)
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

            Traverse.Create(motor).Property("MoveDirection").SetValue(new Trilean2(s.LookX, s.LookY));
        }
    }
}
