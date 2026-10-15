using System.IO;
using UnityEngine;

namespace CupheadOnline.UI
{
    /// <summary>
    /// Developer preview for the multiplayer overlays. With
    /// BepInEx/config/cupheads-uipreview.flag present, Home toggles every
    /// in-game overlay on with sample data (partner "Daniel", 42 ms), so their
    /// layout can be checked without a second player. Off otherwise.
    /// </summary>
    public static class UiPreview
    {
        static bool? _enabled;
        static bool _toastSent;
        static int _step;

        public static bool Overlays { get; private set; }

        public const string PartnerName = "DANIEL";
        public const string PartnerCharacter = "Mugman";
        public const int Ping = 42;

        static bool Enabled
        {
            get
            {
                if (!_enabled.HasValue)
                {
                    try { _enabled = File.Exists(Path.Combine(BepInEx.Paths.ConfigPath, "cupheads-uipreview.flag")); }
                    catch { _enabled = false; }
                }
                return _enabled.Value;
            }
        }

        public static void Tick()
        {
            if (!Enabled || !Input.GetKeyDown(KeyCode.Home))
                return;

            // off → fight overlays → results card → off
            _step = (_step + 1) % 3;
            Overlays = _step == 1;
            Plugin.Log.LogInfo("[UiPreview] Step " + _step);
            if (_step == 1)
            {
                ConnectionHUD.Show();
                SessionPausePanel.Ensure();
                if (!_toastSent)
                {
                    _toastSent = true;
                    CommWheel.ShowPreviewToast();
                }
            }
            else if (_step == 2)
            {
                WinStatsCard.ShowPreview();
            }
            else
            {
                WinStatsCard.Hide();
                _toastSent = false;
            }
        }
    }
}
