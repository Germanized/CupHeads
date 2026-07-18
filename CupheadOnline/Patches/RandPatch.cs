using HarmonyLib;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Replaces Cuphead's built-in random helpers with a seeded deterministic PRNG
    /// while a network session is active. Both host and client are seeded
    /// identically via SceneChangePacket, so RNG-dependent decisions that flow
    /// through Rand (chalice-charm blocking, intro variants, coin flips) resolve
    /// the same way on both machines.
    ///
    /// Cuphead's Rand module only exposes Bool() and PosOrNeg(); everything else
    /// in the game calls UnityEngine.Random directly and is covered by the
    /// host-authoritative enemy state sync instead.
    /// </summary>
    [HarmonyPatch(typeof(Rand), "Bool")]
    public static class RandPatch
    {
        static bool Prefix(ref bool __result)
        {
            if (!MultiplayerSession.IsActive) return true;
            if (!RngSync.IsSeeded) return true;

            __result = RngSync.NextBool();
            return false; // skip original
        }
    }

    [HarmonyPatch(typeof(Rand), "PosOrNeg")]
    public static class RandIntPatch
    {
        static bool Prefix(ref int __result)
        {
            if (!MultiplayerSession.IsActive) return true;
            if (!RngSync.IsSeeded) return true;

            __result = RngSync.NextBool() ? 1 : -1;
            return false;
        }
    }
}
