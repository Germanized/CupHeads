using System;
using HarmonyLib;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Field-ref delegates are created in static initialisers of patch classes.
    /// If a game update renamed a field, AccessTools would throw there, turning
    /// every call of the hooked game method into a TypeInitializationException.
    /// These helpers log once and return null instead; callers skip the
    /// feature that needs the field.
    /// </summary>
    internal static class SafeAccess
    {
        internal static AccessTools.FieldRef<T, F> FieldRef<T, F>(string fieldName) where T : class
        {
            try
            {
                return AccessTools.FieldRefAccess<T, F>(fieldName);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[SafeAccess] " + typeof(T).Name + "." + fieldName + " unavailable: " + ex.Message);
                return null;
            }
        }
    }
}
