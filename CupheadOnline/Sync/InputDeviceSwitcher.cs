using System;
using System.Reflection;
using HarmonyLib;
using Rewired;
using UnityEngine;
using UnityEngine.SceneManagement;
using CupheadOnline.UI;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Use the keyboard or any controller at any time.
    ///
    /// Cuphead gives Player One only the device that pressed a button on the
    /// title screen, and keeps every other controller unassigned so that a
    /// press on it means "Player Two joins". So someone who started on the
    /// keyboard could never pick up a controller later (and online, where the
    /// second player is the remote partner, the spare controller did nothing).
    ///
    /// This runs just before the game's own join check each frame
    /// (PlayerManager.Update), so the press that hands a controller over can't
    /// also start a join:
    ///   • Online: any controller pressed is handed to this machine's player
    ///     (the mod already merges both local Rewired players' input).
    ///   • Offline: a pressed spare controller goes to Player One while Player
    ///     One is on the keyboard and nobody has joined as Player Two. A second
    ///     controller is left alone, so a couch Player Two can still join.
    /// The keyboard keeps working alongside, and the button prompts follow
    /// whichever device was used last (the game's own last-controller logic).
    /// </summary>
    public static class InputDeviceSwitcher
    {
        static readonly FieldInfo PlayerSlotsField = AccessTools.Field(typeof(PlayerManager), "playerSlots");
        static string _loggedScene = string.Empty;

        /// <summary>Prefix of PlayerManager.Update.</summary>
        public static void BeforeGameInputUpdate()
        {
            if (!Plugin.SwitchInputAnytime)
                return;

            try
            {
                LogAssignmentsOncePerScene();

                if (InterruptingPrompt.IsInterrupting())
                    return; // the reconnect prompt handles its own device choice

                bool online = MultiplayerSession.IsActive && !LocalDevSession.IsActive;
                if (online)
                {
                    EnsureKeyboard(PlayerId.PlayerOne);
                    AdoptPressedController(PlayerId.PlayerOne, updateSlot: false);
                    return;
                }

                // Offline: only Player One on the keyboard, solo, past the title screen.
                if (PlayerManager.Multiplayer || !IsJoined(0))
                    return;
                var playerOne = PlayerManager.GetPlayerInput(PlayerId.PlayerOne);
                if (playerOne == null || playerOne.controllers.joystickCount > 0)
                    return;
                AdoptPressedController(PlayerId.PlayerOne, updateSlot: true);
            }
            catch (Exception ex)
            {
                Plugin.LogVerbose("[Input] Device switch skipped: " + ex.Message);
            }
        }

        static void AdoptPressedController(PlayerId target, bool updateSlot)
        {
            Joystick joystick = CupheadInput.CheckForUnconnectedControllerPress();
            if (joystick != null)
                Adopt(joystick, target, updateSlot);
        }

        /// <summary>
        /// UI tour: hand the first spare controller over exactly as a button
        /// press on it would (offline rules). False if there is none.
        /// </summary>
        internal static bool TestHandOverFirstSpareController()
        {
            foreach (var joystick in ReInput.controllers.Joysticks)
            {
                if (joystick == null || ReInput.controllers.IsJoystickAssigned(joystick))
                    continue;
                Adopt(joystick, PlayerId.PlayerOne, updateSlot: true);
                return true;
            }
            return false;
        }

        static void Adopt(Joystick joystick, PlayerId target, bool updateSlot)
        {
            var player = PlayerManager.GetPlayerInput(target);
            if (player == null)
                return;

            player.controllers.AddController(joystick, true);
            try { ReInput.userDataStore.LoadControllerData(player.id, ControllerType.Joystick, joystick.id); }
            catch { }

            if (updateSlot)
                SetSlotController(0, joystick.id);

            PlayerManager.ControlsChanged();
            Plugin.Log.LogInfo("[Input] " + (joystick.name ?? "Controller") + " is now driving " + target + " (keyboard still works).");
            ConnectionHUD.Show("Controller ready! Your keyboard still works too.");
        }

        /// <summary>Online: make sure some local Rewired player owns the keyboard.</summary>
        static void EnsureKeyboard(PlayerId target)
        {
            var one = PlayerManager.GetPlayerInput(PlayerId.PlayerOne);
            var two = PlayerManager.GetPlayerInput(PlayerId.PlayerTwo);
            if ((one != null && one.controllers.hasKeyboard) || (two != null && two.controllers.hasKeyboard))
                return;

            var player = PlayerManager.GetPlayerInput(target);
            if (player == null)
                return;
            player.controllers.hasKeyboard = true;
            Plugin.Log.LogInfo("[Input] Keyboard re-enabled for this player.");
        }

        // ── PlayerManager slot bookkeeping (private nested class) ─────────────

        static object Slot(int index)
        {
            var slots = PlayerSlotsField != null ? PlayerSlotsField.GetValue(null) as Array : null;
            return slots != null && index < slots.Length ? slots.GetValue(index) : null;
        }

        static bool IsJoined(int index)
        {
            var slot = Slot(index);
            if (slot == null)
                return false;
            var field = AccessTools.Field(slot.GetType(), "joinState");
            return field != null && field.GetValue(slot).ToString() == "Joined";
        }

        /// <summary>
        /// Records the adopted controller the way the game does for a controller
        /// player, so unplugging it brings up the usual reconnect prompt.
        /// </summary>
        static void SetSlotController(int index, int controllerId)
        {
            var slot = Slot(index);
            if (slot == null)
                return;
            var state = AccessTools.Field(slot.GetType(), "controllerState");
            var id = AccessTools.Field(slot.GetType(), "controllerId");
            if (state != null)
                state.SetValue(slot, Enum.Parse(state.FieldType, "UsingController"));
            if (id != null)
                id.SetValue(slot, controllerId);
        }

        static void LogAssignmentsOncePerScene()
        {
            string scene = SceneManager.GetActiveScene().name ?? string.Empty;
            if (scene == _loggedScene)
                return;
            _loggedScene = scene;
            Plugin.Log.LogInfo("[Input] " + scene + ": " + Describe());
        }

        /// <summary>Which devices each Rewired player owns, for logs and reports.</summary>
        public static string Describe()
        {
            try
            {
                var one = PlayerManager.GetPlayerInput(PlayerId.PlayerOne);
                var two = PlayerManager.GetPlayerInput(PlayerId.PlayerTwo);
                int connected = ReInput.controllers.joystickCount;
                string names = string.Empty;
                foreach (var joystick in ReInput.controllers.Joysticks)
                {
                    if (joystick == null) continue;
                    names += (names.Length > 0 ? ", " : " (") + joystick.name
                        + (ReInput.controllers.IsJoystickAssigned(joystick) ? " assigned" : " spare");
                }
                if (names.Length > 0) names += ")";

                string slot = string.Empty;
                var slotOne = Slot(0);
                if (slotOne != null)
                {
                    var state = AccessTools.Field(slotOne.GetType(), "controllerState");
                    if (state != null)
                        slot = "; P1 slot " + state.GetValue(slotOne);
                }

                return "P1 keyboard=" + (one != null && one.controllers.hasKeyboard)
                    + " pads=" + (one != null ? one.controllers.joystickCount : 0)
                    + "; P2 keyboard=" + (two != null && two.controllers.hasKeyboard)
                    + " pads=" + (two != null ? two.controllers.joystickCount : 0)
                    + "; controllers connected=" + connected + names + slot;
            }
            catch (Exception ex)
            {
                return "unavailable (" + ex.Message + ")";
            }
        }
    }
}
