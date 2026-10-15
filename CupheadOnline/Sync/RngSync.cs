using System;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Provides a deterministic, seeded PRNG that replaces Cuphead's built-in
    /// Rand calls while a network session is active.
    ///
    /// Both host and client are seeded identically at scene load via SceneChangePacket,
    /// ensuring boss attack patterns, spawn timings, and any other RNG-dependent
    /// behaviour unfolds identically on both machines.
    ///
    /// The underlying algorithm is a 64-bit xorshift+ (extremely fast, no alloc).
    /// </summary>
    public static class RngSync
    {
        private static ulong _s0;
        private static ulong _s1;

        public static bool IsSeeded { get; private set; }
        public static uint CurrentSeed { get; private set; }

        // ──────────────────────────────────────────────────────────────────────
        //  Seeding
        // ──────────────────────────────────────────────────────────────────────

        public static void SetSeed(uint seed)
        {
            bool changed = !IsSeeded || seed != CurrentSeed;
            CurrentSeed = seed;
            ResetStream(seed);
            IsSeeded = true;
            if (changed)
                Plugin.Log.LogInfo($"[RngSync] Seeded with {seed:X8}");
        }

        /// <summary>
        /// Called as a level's scene comes up (Level.Awake) on both peers. Most
        /// boss behaviour rolls UnityEngine.Random directly, so seeding Unity's
        /// generator with the shared scene seed here makes both machines pick
        /// the same opening patterns, and restarting the Rand stream keeps the
        /// game's own coin flips aligned from the same point.
        /// </summary>
        public static void SeedLevelStart(int levelEnum)
        {
            if (!IsSeeded)
                return;

            uint mixed = unchecked(CurrentSeed ^ (uint)levelEnum * 0x9E3779B1u);
            UnityEngine.Random.InitState(unchecked((int)mixed));
            ResetStream(CurrentSeed);
            Plugin.Log.LogInfo($"[RngSync] Level {levelEnum} seeded from {CurrentSeed:X8}.");
        }

        static void ResetStream(uint seed)
        {
            // SplitMix64 to expand the 32-bit seed into two 64-bit states
            _s0 = SplitMix64((ulong)seed);
            _s1 = SplitMix64(_s0);
        }

        /// <summary>
        /// Generate a new random seed for the next SceneChangePacket and seed the
        /// LOCAL PRNG with it too, so the sender (host) runs the same sequence as
        /// the peer that receives the seed.
        /// </summary>
        public static uint NextSeed()
        {
            unchecked
            {
                _seedCounter++;
                ulong mixed = SplitMix64((ulong)DateTime.UtcNow.Ticks ^ ((ulong)_seedCounter << 32));
                var seed = (uint)(mixed ^ (mixed >> 32));
                if (seed == 0u)
                    seed = 0x6C8E9CF5u;
                SetSeed(seed);
                return seed;
            }
        }

        static uint _seedCounter;

        // ──────────────────────────────────────────────────────────────────────
        //  Generation
        // ──────────────────────────────────────────────────────────────────────

        public static float NextFloat(float min, float max)
        {
            if (!IsSeeded) return UnityEngine.Random.Range(min, max);
            double t = (NextRaw() >> 11) * (1.0 / (1ul << 53));
            return min + (float)(t * (max - min));
        }

        public static int NextInt(int min, int max)
        {
            if (!IsSeeded) return UnityEngine.Random.Range(min, max);
            if (min >= max) return min;
            return min + (int)(NextRaw() % (uint)(max - min));
        }

        public static bool NextBool()
        {
            if (!IsSeeded) return UnityEngine.Random.Range(0, 2) == 1;
            return (NextRaw() & 1UL) != 0UL;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  xorshift128+
        // ──────────────────────────────────────────────────────────────────────

        static ulong NextRaw()
        {
            ulong s1 = _s0;
            ulong s0 = _s1;
            ulong result = s0 + s1;
            _s0 = s0;
            s1 ^= s1 << 23;
            _s1  = s1 ^ s0 ^ (s1 >> 17) ^ (s0 >> 26);
            return result;
        }

        static ulong SplitMix64(ulong x)
        {
            x += 0x9e3779b97f4a7c15UL;
            x  = (x ^ (x >> 30)) * 0xbf58476d1ce4e5b9UL;
            x  = (x ^ (x >> 27)) * 0x94d049bb133111ebUL;
            return x ^ (x >> 31);
        }
    }
}
