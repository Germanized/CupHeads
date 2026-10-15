using HarmonyLib;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Runs the device switch just before the game's own join check, so a
    /// controller press handed to the current player can't also start a
    /// Player Two join (InputDeviceSwitcher).
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), "Update")]
    public static class PlayerManagerInputSwitchPatch
    {
        static void Prefix()
        {
            InputDeviceSwitcher.BeforeGameInputUpdate();
        }
    }
}
