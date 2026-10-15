using HarmonyLib;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Seeds UnityEngine.Random with the host's scene seed as the level comes
    /// up, before boss scripts roll their opening patterns.
    /// </summary>
    [HarmonyPatch(typeof(Level), "Awake")]
    public static class LevelAwakeSeedPatch
    {
        static void Prefix(Level __instance)
        {
            if (!MultiplayerSession.IsActive || LocalDevSession.IsActive || __instance == null)
                return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            int levelEnum;
            try { levelEnum = (int)__instance.CurrentLevel; }
            catch { return; }

            RngSync.SeedLevelStart(levelEnum);
        }
    }

    /// <summary>Host-authoritative knockout gate (see LevelOutcomeSync).</summary>
    [HarmonyPatch(typeof(Level), "zHack_OnWin")]
    public static class LevelWinGatePatch
    {
        static bool Prefix(Level __instance)
        {
            if (!MultiplayerSession.IsActive)
                return true;

            return LevelOutcomeSync.AllowWin(__instance);
        }
    }

    [HarmonyPatch(typeof(AbstractPauseGUI), "PauseGameplay")]
    public static class PauseGuiPauseGameplayPatch
    {
        static void Postfix(AbstractPauseGUI __instance)
        {
            PauseSync.OnLocalPauseMenuOpened(__instance);
        }
    }

    [HarmonyPatch(typeof(AbstractPauseGUI), "UnpauseGameplay")]
    public static class PauseGuiUnpauseGameplayPatch
    {
        static bool Prefix(AbstractPauseGUI __instance)
        {
            return PauseSync.OnLocalPauseMenuClosing(__instance);
        }
    }
}
