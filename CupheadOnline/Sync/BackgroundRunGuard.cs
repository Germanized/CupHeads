using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Keeps the game running while it is alt-tabbed during an online lobby or
    /// session. If Unity stops updating an unfocused window, this machine stops
    /// sending and receiving, and the partner sees a frozen game or a timeout.
    /// The original setting is restored when the session ends.
    /// </summary>
    public static class BackgroundRunGuard
    {
        static bool _forced;
        static bool _original;

        public static void Tick()
        {
            var net = Plugin.Net;
            bool online = net != null && (net.IsConnected || net.IsInLobby);

            if (online && !_forced)
            {
                _original = Application.runInBackground;
                _forced = true;
                if (!_original)
                {
                    Application.runInBackground = true;
                    Plugin.Log.LogInfo("[Session] Keeping the game running while alt-tabbed during the session.");
                }
            }
            else if (!online && _forced)
            {
                _forced = false;
                if (Application.runInBackground != _original)
                    Application.runInBackground = _original;
            }
        }
    }
}
