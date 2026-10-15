using HarmonyLib;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    /// <summary>
    /// Every save in Cuphead funnels through PlayerData.Save(fileIndex). The
    /// guest is playing on a mirror of the host's save, so its writes are
    /// dropped (its own save file stays exactly as it was before the session);
    /// the host re-broadcasts the mirror after each save.
    /// </summary>
    [HarmonyPatch(typeof(PlayerData), "Save")]
    public static class PlayerDataSavePatch
    {
        static bool Prefix()
        {
            return !SaveMirror.BlockLocalDiskSaves;
        }

        static void Postfix()
        {
            if (MultiplayerSession.IsHost)
                SaveMirror.QueueHostBroadcast("host saved");
        }
    }

    [HarmonyPatch(typeof(PlayerData), "SaveAll")]
    public static class PlayerDataSaveAllPatch
    {
        static bool Prefix()
        {
            return !SaveMirror.BlockLocalDiskSaves;
        }
    }
}
