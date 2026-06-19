using System.Reflection;
using HarmonyLib;

namespace CupheadOnline.Patches
{
    [HarmonyPatch(typeof(AbstractEquipUI), "Close")]
    public static class AbstractEquipUICloseOnlinePatch
    {
        static readonly FieldInfo PlayerOneField =
            AccessTools.Field(typeof(AbstractEquipUI), "playerOne");
        static readonly FieldInfo PlayerTwoField =
            AccessTools.Field(typeof(AbstractEquipUI), "playerTwo");

        static void Prefix(AbstractEquipUI __instance)
        {
            if (!MultiplayerSession.IsActive || __instance == null)
                return;

            try
            {
                if (__instance.CurrentState != AbstractEquipUI.ActiveState.Active)
                    return;
            }
            catch
            {
                return;
            }

            // Online play has no safe way to wait on mismatched local UI states.
            // Mark both cards ready before vanilla Close() runs so any player can
            // back out instead of soft-locking the map/equipment menu.
            MarkReady(PlayerOneField == null ? null : PlayerOneField.GetValue(__instance));
            MarkReady(PlayerTwoField == null ? null : PlayerTwoField.GetValue(__instance));
        }

        static void MarkReady(object card)
        {
            var equipCard = card as MapEquipUICard;
            if (equipCard != null)
                Traverse.Create(equipCard).Property("ReadyAndWaiting").SetValue(true);
        }
    }
}
