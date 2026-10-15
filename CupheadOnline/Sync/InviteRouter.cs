using System;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;
using CupheadOnline.UI;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Every way into someone's lobby goes through here: an invite accepted
    /// in the Steam overlay or chat, "Join Game" on the friends list, and
    /// launching Cuphead from an invite while the game is closed (Steam then
    /// passes "+connect_lobby &lt;id&gt;" on the command line).
    ///
    /// A join only starts once this player's own saves are loaded (the save
    /// slot screen has initialised PlayerData) and not from the title screen.
    /// Joining earlier let the save mirror record the not-yet-loaded, empty
    /// slots as the guest's "originals" and put them back after the session,
    /// where a later save could write them over the guest's real progress.
    /// An early invite is held, the player is told, and the lobby opens by
    /// itself on the main menu.
    /// </summary>
    public static class InviteRouter
    {
        static ulong _pendingLobby;
        static string _pendingFrom = string.Empty;
        static bool _openLobbyRequested;
        static bool _commandLineChecked;

        /// <summary>Once, after Steam is up: handle "+connect_lobby" from a cold launch.</summary>
        public static void CheckCommandLine()
        {
            if (_commandLineChecked)
                return;
            _commandLineChecked = true;

            string[] args;
            try { args = Environment.GetCommandLineArgs(); }
            catch { return; }

            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (!string.Equals(args[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase))
                    continue;

                ulong lobby;
                if (ulong.TryParse(args[i + 1], out lobby) && lobby != 0UL)
                {
                    Plugin.Log.LogInfo("[Invite] Cuphead was launched from a Steam invite (lobby " + lobby + ").");
                    OnInviteAccepted(lobby, string.Empty);
                }
                return;
            }
        }

        /// <summary>An invite was accepted for this lobby.</summary>
        public static void OnInviteAccepted(ulong lobby, string fromName)
        {
            if (lobby == 0UL || Plugin.Net == null)
                return;

            if (fromName == "Unknown Player")
                fromName = string.Empty;

            if (CanJoinNow())
            {
                Join(lobby, fromName);
                return;
            }

            _pendingLobby = lobby;
            _pendingFrom = fromName ?? string.Empty;
            Plugin.Log.LogInfo("[Invite] Holding the invite until the save slots are loaded.");
            ConnectionHUD.Show((string.IsNullOrEmpty(_pendingFrom) ? "Invite accepted!" : "Invite from " + _pendingFrom + " accepted!")
                + " Your lobby opens on the main menu.");
        }

        /// <summary>Per frame: start a held join once it is safe.</summary>
        public static void Tick()
        {
            if (_pendingLobby == 0UL || !CanJoinNow())
                return;

            ulong lobby = _pendingLobby;
            string from = _pendingFrom;
            _pendingLobby = 0UL;
            _pendingFrom = string.Empty;
            Join(lobby, from);
        }

        /// <summary>The slot-select menu asks once per frame whether to open the lobby.</summary>
        public static bool ConsumeOpenLobbyRequest()
        {
            if (!_openLobbyRequested)
                return false;
            _openLobbyRequested = false;
            return true;
        }

        public static bool HasPendingInvite => _pendingLobby != 0UL;

        static void Join(ulong lobby, string fromName)
        {
            Plugin.Log.LogInfo("[Invite] Joining lobby " + lobby + ".");
            if (!Plugin.Net.JoinLobby(new CSteamID(lobby)))
            {
                ConnectionHUD.Show("Couldn't join: Steam isn't ready.");
                return;
            }

            if (IsOnSlotSelect())
                _openLobbyRequested = !CupheadOnline.Patches.MpMenuState.InMpMenu;
            else
                ConnectionHUD.Show(string.IsNullOrEmpty(fromName) ? "Joining your friend's game..." : "Joining " + fromName + "'s game...");
        }

        static bool CanJoinNow()
        {
            if (!PlayerData.Initialized)
                return false;

            string scene = ActiveScene();
            if (scene == "scene_title" || scene == "scene_start" || string.IsNullOrEmpty(scene))
                return false;

            try { return !SceneLoader.CurrentlyLoading; }
            catch { return false; }
        }

        static bool IsOnSlotSelect()
        {
            return ActiveScene() == "scene_slot_select";
        }

        static string ActiveScene()
        {
            try { return SceneManager.GetActiveScene().name ?? string.Empty; }
            catch { return string.Empty; }
        }
    }
}
