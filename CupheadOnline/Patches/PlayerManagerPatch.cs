using HarmonyLib;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Hooks into the player lifecycle so we can:
    ///   1. Force PlayerManager.Multiplayer = true while a network session is active.
    ///   2. Subscribe weapon-event listeners after both players are initialised.
    ///   3. Apply the remote player's loadout (received via LobbySyncPacket).
    /// </summary>

    // ── Force Multiplayer flag true while session is active ──────────────────
    [HarmonyPatch(typeof(PlayerManager), "Awake")]
    public static class PlayerManagerAwakePatch
    {
        static void Postfix()
        {
            MultiplayerSession.EnsureCupheadMultiplayerState();
        }
    }

    [HarmonyPatch(typeof(Map), "Awake")]
    public static class MapAwakePatch
    {
        static void Prefix()
        {
            MultiplayerSession.EnsureCupheadMultiplayerState();
        }
    }

    [HarmonyPatch(typeof(Map), "CreatePlayers")]
    public static class MapCreatePlayersPatch
    {
        static void Prefix()
        {
            MultiplayerSession.EnsureCupheadMultiplayerState();
        }
    }

    // ── Subscribe events after a player is fully initialised ─────────────────
    [HarmonyPatch(typeof(AbstractPlayerController), "LevelInit")]
    public static class PlayerLevelInitPatch
    {
        static void Postfix(AbstractPlayerController __instance)
        {
            if (!MultiplayerSession.IsActive) return;
            if (__instance is LevelPlayerController lpc)
                WeaponManagerPatch.Subscribe(lpc);
        }
    }

    // ── Apply remote loadout when PlayerStatsManager initialises ────────────
    [HarmonyPatch(typeof(PlayerStatsManager), "LevelInit")]
    public static class StatsLevelInitPatch
    {
        static void Prefix(PlayerStatsManager __instance)
        {
            if (!MultiplayerSession.IsActive) return;
            var player = __instance.GetComponent<AbstractPlayerController>();
            if (player == null) return;
            if (MultiplayerSession.IsNetworkControlledPlayer(player.id))
                LoadoutReplicator.PreparePendingForStats(player.id);
        }

        static void Postfix(PlayerStatsManager __instance)
        {
            if (!MultiplayerSession.IsActive) return;
            var player = __instance.GetComponent<AbstractPlayerController>();
            if (player == null) return;
            if (MultiplayerSession.IsNetworkControlledPlayer(player.id))
                LoadoutReplicator.ApplyPreparedStats(__instance, player.id);
        }
    }

    // ── Ensure both players are spawned when the level starts ────────────────
    [HarmonyPatch(typeof(Level), "Start")]
    public static class LevelStartPatch
    {
        static void Prefix()
        {
            MultiplayerSession.EnsureCupheadMultiplayerState();
        }

        static void Postfix()
        {
            // Guest reports the level loaded; host holds the intro until the
            // guest's report arrives (or a timeout passes).
            LevelReadyGate.OnLevelStarted();
        }
    }
}
