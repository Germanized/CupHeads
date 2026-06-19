using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    [HarmonyPatch(typeof(LevelPlayerWeaponManager.WeaponPrefabs), "GetWeapon")]
    public static class WeaponPrefabGetWeaponFallbackPatch
    {
        static readonly FieldInfo WeaponsField =
            AccessTools.Field(typeof(LevelPlayerWeaponManager.WeaponPrefabs), "weapons");

        static readonly HashSet<int> WarnedWeaponIds = new HashSet<int>();

        static bool Prefix(LevelPlayerWeaponManager.WeaponPrefabs __instance, Weapon weapon, ref AbstractLevelWeapon __result)
        {
            var weapons = WeaponsField == null
                ? null
                : WeaponsField.GetValue(__instance) as Dictionary<Weapon, AbstractLevelWeapon>;

            if (weapons == null)
                return true;

            AbstractLevelWeapon resolved;
            if (weapons.TryGetValue(weapon, out resolved) && resolved != null)
            {
                __result = resolved;
                return false;
            }

            var fallback = LoadoutCodec.NormalizeWeapon(weapon, primarySlot: true);
            if (fallback != weapon && weapons.TryGetValue(fallback, out resolved) && resolved != null)
            {
                WarnOnce(weapon, fallback);
                __result = resolved;
                return false;
            }

            if (weapons.TryGetValue(Weapon.level_weapon_peashot, out resolved) && resolved != null)
            {
                WarnOnce(weapon, Weapon.level_weapon_peashot);
                __result = resolved;
                return false;
            }

            foreach (var pair in weapons)
            {
                if (pair.Value == null)
                    continue;

                WarnOnce(weapon, pair.Key);
                __result = pair.Value;
                return false;
            }

            return true;
        }

        static void WarnOnce(Weapon requested, Weapon fallback)
        {
            int key = (int)requested;
            if (WarnedWeaponIds.Contains(key))
                return;

            WarnedWeaponIds.Add(key);
            Plugin.Log.LogWarning("[WeaponSafety] Invalid weapon " + requested + " (" + key + ") fell back to " + fallback + ".");
        }
    }
}
