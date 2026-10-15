using HarmonyLib;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    [HarmonyPatch(typeof(AbstractPlayerController), "OnDeath")]
    public static class PlayerDeathStatePatch
    {
        static void Prefix(AbstractPlayerController __instance)
        {
            TrySendImmediateState(__instance);
        }

        internal static void TrySendImmediateState(AbstractPlayerController player)
        {
            if (!MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected || player == null)
                return;
            if (!MultiplayerSession.IsAuthoritativePlayer(player.id))
                return;

            var levelPlayer = player as LevelPlayerController;
            if (levelPlayer == null || levelPlayer.motor == null)
                return;

            var pkt = PlayerMotorPatch.BuildStatePacket(levelPlayer, levelPlayer.motor);
            Plugin.Net.SendPlayerState(ref pkt);
            if (MultiplayerSession.IsBodyOwnedLocally(player.id))
                ParticipantStatusTracker.PushLocalStatus(player);
        }
    }

    [HarmonyPatch(typeof(AbstractPlayerController), "OnRevive")]
    public static class PlayerReviveStatePatch
    {
        static void Postfix(AbstractPlayerController __instance)
        {
            PlayerDeathStatePatch.TrySendImmediateState(__instance);
        }
    }
}
