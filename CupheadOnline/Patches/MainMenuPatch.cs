using HarmonyLib;
using CupheadOnline.UI;

namespace CupheadOnline.Patches
{
    // The MULTIPLAYER entry lives in the save-select main menu
    // (SlotSelectScreenPatch); the title screen only needs the splash gates.

    [HarmonyPatch(typeof(StartScreen), "Update")]
    public static class StartScreenSplashGatePatch
    {
        static bool Prefix()
        {
            return !StartupSplashPlayer.IsBlockingGame;
        }
    }

    [HarmonyPatch(typeof(StartScreenAudio), "Update")]
    public static class StartScreenAudioSplashGatePatch
    {
        static bool Prefix()
        {
            return !StartupSplashPlayer.IsBlockingGame;
        }
    }
}
