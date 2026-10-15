using HarmonyLib;
using CupheadOnline.Sync;
using CupheadOnline.UI;

namespace CupheadOnline.Patches
{
    // ── King Dice board: host-authoritative roll (DicePalaceSync) ────────────

    [HarmonyPatch(typeof(DicePalaceMainLevelDice), "RollOne")]
    public static class DiceRollOnePatch
    {
        static bool Prefix() => DicePalaceSync.AllowLocalRoll();
        static void Postfix(DicePalaceMainLevelDice __instance) => DicePalaceSync.OnLocalRoll(__instance);
    }

    [HarmonyPatch(typeof(DicePalaceMainLevelDice), "RollTwo")]
    public static class DiceRollTwoPatch
    {
        static bool Prefix() => DicePalaceSync.AllowLocalRoll();
        static void Postfix(DicePalaceMainLevelDice __instance) => DicePalaceSync.OnLocalRoll(__instance);
    }

    [HarmonyPatch(typeof(DicePalaceMainLevelDice), "RollThree")]
    public static class DiceRollThreePatch
    {
        static bool Prefix() => DicePalaceSync.AllowLocalRoll();
        static void Postfix(DicePalaceMainLevelDice __instance) => DicePalaceSync.OnLocalRoll(__instance);
    }

    /// <summary>
    /// The co-op pause menu's "Player 2 leave" drops the second player from the
    /// game. Online that is the remote partner, and removing them leaves both
    /// machines in a broken one-player state; leaving is done from the lobby.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), "PlayerLeave")]
    public static class PlayerManagerPlayerLeaveBlockPatch
    {
        static bool Prefix()
        {
            if (!MultiplayerSession.IsActive)
                return true;

            ConnectionHUD.Show("Online partners can't be dropped here. Use LEAVE in the Multiplayer menu.");
            return false;
        }
    }
}
