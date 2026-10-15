using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Copies Cuphead's save files (%AppData%\Cuphead\*.sav) to
    /// BepInEx\CupHeads\SaveBackups\&lt;date-time&gt;\ whenever an online
    /// session starts, keeping the newest few. An online run writes the host's
    /// save as usual, so this is a cheap safety net against any sync bug. A
    /// backup identical to the newest one is skipped.
    /// To restore: close Cuphead and copy a backup's .sav files back into
    /// %AppData%\Cuphead.
    /// </summary>
    public static class SaveBackups
    {
        static bool _initialized;

        public static string BackupRoot =>
            Path.Combine(Path.Combine(BepInEx.Paths.BepInExRootPath, "CupHeads"), "SaveBackups");

        static string SaveFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cuphead");

        public static void Initialize()
        {
            if (_initialized)
                return;
            _initialized = true;
            MultiplayerSession.OnSessionStarted += () => BackupNow("online session");
        }

        /// <summary>Back up the save files now. Returns the backup folder, or null.</summary>
        public static string BackupNow(string reason)
        {
            if (!Plugin.BackupSavesBeforeSessions || LocalDevSession.IsActive)
                return null;

            try
            {
                if (!Directory.Exists(SaveFolder))
                    return null;

                string[] saves = Directory.GetFiles(SaveFolder, "*.sav");
                if (saves.Length == 0)
                    return null;

                Directory.CreateDirectory(BackupRoot);
                if (SameAsNewestBackup(saves))
                {
                    Plugin.LogVerbose("[Backups] Saves unchanged since the last backup.");
                    return null;
                }

                string folder = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                Directory.CreateDirectory(folder);
                foreach (string save in saves)
                    File.Copy(save, Path.Combine(folder, Path.GetFileName(save)), true);

                Prune();
                Plugin.Log.LogInfo("[Backups] Saved a copy of " + saves.Length + " save file(s) before the " + reason + ": " + folder);
                return folder;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Backups] Could not back up the saves: " + ex.Message);
                return null;
            }
        }

        static bool SameAsNewestBackup(string[] saves)
        {
            string newest = NewestBackup();
            if (newest == null)
                return false;

            foreach (string save in saves)
            {
                string copy = Path.Combine(newest, Path.GetFileName(save));
                if (!File.Exists(copy))
                    return false;
                if (!SameBytes(File.ReadAllBytes(save), File.ReadAllBytes(copy)))
                    return false;
            }
            return true;
        }

        static string NewestBackup()
        {
            var folders = SortedBackups();
            return folders.Count == 0 ? null : folders[folders.Count - 1];
        }

        /// <summary>Backup folders, oldest first (their names sort by time).</summary>
        static List<string> SortedBackups()
        {
            var folders = new List<string>();
            if (!Directory.Exists(BackupRoot))
                return folders;
            folders.AddRange(Directory.GetDirectories(BackupRoot));
            folders.Sort(StringComparer.Ordinal);
            return folders;
        }

        static void Prune()
        {
            var folders = SortedBackups();
            int keep = Mathf.Max(1, Plugin.SaveBackupsToKeep);
            for (int i = 0; i < folders.Count - keep; i++)
            {
                try { Directory.Delete(folders[i], true); }
                catch (Exception ex) { Plugin.LogVerbose("[Backups] Could not remove " + folders[i] + ": " + ex.Message); }
            }
        }

        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
