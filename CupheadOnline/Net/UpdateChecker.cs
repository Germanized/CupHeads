using System;
using System.Collections;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace CupheadOnline.Net
{
    /// <summary>
    /// Once per launch, asks GitHub for the newest CupHeads release so the main
    /// menu and lobby can say when this build is out of date. Players on
    /// different versions cannot connect, so this catches a stale install
    /// before a session fails. Read-only (a single GET of the public release
    /// listing); turn it off with [Updates] CheckForUpdates = false.
    /// </summary>
    public static class UpdateChecker
    {
        public const string ReleasesPage = "https://github.com/Germanized/CupHeads/releases/latest";
        const string LatestReleaseApi = "https://api.github.com/repos/Germanized/CupHeads/releases/latest";

        static bool _started;

        /// <summary>The newest release's version (e.g. "1.6.1"), once known.</summary>
        public static string LatestVersion { get; private set; }

        public static bool UpdateAvailable { get; private set; }

        public static void Begin(MonoBehaviour host)
        {
            if (_started || host == null || !Plugin.CheckForUpdates)
                return;
            _started = true;
            host.StartCoroutine(Check());
        }

        static IEnumerator Check()
        {
            // Let the game finish booting first.
            yield return new WaitForSecondsRealtime(4f);

            UnityWebRequest request;
            try
            {
                request = UnityWebRequest.Get(LatestReleaseApi);
                request.timeout = 10;
                request.SetRequestHeader("Accept", "application/vnd.github+json");
                try { request.SetRequestHeader("User-Agent", "CupHeads-mod/" + PluginInfo.VERSION); }
                catch { }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Update] Could not start the update check: " + ex.Message);
                yield break;
            }

            yield return request.SendWebRequest();

            try
            {
                if (request.isNetworkError || request.isHttpError)
                {
                    Plugin.Log.LogInfo("[Update] Update check skipped (" + (request.error ?? "HTTP " + request.responseCode) + ").");
                    yield break;
                }

                var match = Regex.Match(request.downloadHandler.text ?? string.Empty, "\"tag_name\"\\s*:\\s*\"v?([0-9][0-9A-Za-z.\\-]*)\"");
                if (!match.Success)
                {
                    Plugin.Log.LogInfo("[Update] Update check: no release tag found.");
                    yield break;
                }

                LatestVersion = match.Groups[1].Value;
                UpdateAvailable = IsNewer(LatestVersion, PluginInfo.VERSION);
                Plugin.Log.LogInfo(UpdateAvailable
                    ? "[Update] CupHeads v" + LatestVersion + " is available (this is v" + PluginInfo.VERSION + ")."
                    : "[Update] CupHeads is up to date (v" + PluginInfo.VERSION + "; newest release v" + LatestVersion + ").");

                // Developer preview (UI preview flag present): show the notice.
                if (UI.UiPreview.FlagPresent && !UpdateAvailable)
                {
                    LatestVersion = "9.9.9";
                    UpdateAvailable = true;
                }
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>True if candidate is a higher numeric version than current.</summary>
        public static bool IsNewer(string candidate, string current)
        {
            int[] a = Parse(candidate);
            int[] b = Parse(current);
            if (a == null || b == null)
                return false;
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : 0;
                int y = i < b.Length ? b[i] : 0;
                if (x != y)
                    return x > y;
            }
            return false;
        }

        static int[] Parse(string version)
        {
            if (string.IsNullOrEmpty(version))
                return null;
            var parts = version.TrimStart('v', 'V').Split('.');
            var numbers = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                var digits = Regex.Match(parts[i], "^[0-9]+");
                if (!digits.Success || !int.TryParse(digits.Value, out numbers[i]))
                    return null;
            }
            return numbers;
        }
    }
}
