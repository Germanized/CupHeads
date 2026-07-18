using HarmonyLib;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    [HarmonyPatch(typeof(PlayerStatsManager), "LevelInit")]
    public static class PlayerStatsInitialStatusPatch
    {
        static void Postfix(PlayerStatsManager __instance)
        {
            PlayerStatusPatchHelpers.PushLocalStatus(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerStatsManager), "OnHealthChanged")]
    public static class PlayerStatsHealthChangedPatch
    {
        static void Postfix(PlayerStatsManager __instance)
        {
            PlayerStatusPatchHelpers.PushLocalStatus(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerDeathEffect), "ReviveOutOfFrame")]
    public static class PlayerDeathEffectReviveOutOfFramePatch
    {
        static bool Prefix(PlayerDeathEffect __instance)
        {
            return !ParticipantReviveController.TryOverrideReviveOutOfFrame(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerDeathEffect), "Start")]
    public static class PlayerDeathEffectExtraVisualStartPatch
    {
        static void Postfix(PlayerDeathEffect __instance)
        {
            ExtraParticipantReviveVisuals.OnEffectStarted(__instance);
            PlayerColorSync.ApplyDeathEffectTint(__instance);
            GhostDriftController.Register(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerDeathEffect), "OnParrySwitch")]
    public static class PlayerDeathEffectExtraVisualParryPatch
    {
        static bool Prefix(PlayerDeathEffect __instance)
        {
            return !ExtraParticipantReviveVisuals.HandleParrySwitch(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerDeathEffect), "OnReviveParryAnimComplete")]
    public static class PlayerDeathEffectExtraVisualParryAnimPatch
    {
        static bool Prefix(PlayerDeathEffect __instance)
        {
            return !ExtraParticipantReviveVisuals.HandleParryAnimComplete(__instance);
        }
    }

    static class PlayerStatusPatchHelpers
    {
        internal static void PushLocalStatus(PlayerStatsManager stats)
        {
            if (!MultiplayerSession.IsActive || stats == null)
                return;

            var player = stats.GetComponent<AbstractPlayerController>();
            if (player == null)
                return;
            if (!MultiplayerSession.IsAuthoritativePlayer(player.id))
                return;

            ParticipantStatusTracker.PushLocalStatus(player);
        }
    }
}
