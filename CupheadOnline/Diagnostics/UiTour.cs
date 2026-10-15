using System;
using System.Collections;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using CupheadOnline.Patches;
using CupheadOnline.Sync;
using CupheadOnline.UI;

namespace CupheadOnline.Diagnostics
{
    /// <summary>
    /// Developer tool: with BepInEx/config/cupheads-uitour.flag present, the
    /// game drives itself through the CupHeads screens (title → main menu →
    /// shortcuts card → lobby in every phase → in-game overlays), saves a
    /// screenshot of each from its own renderer to BepInEx/CupHeadsUiTour/, and
    /// quits. No keyboard or mouse input is used, and the game keeps running
    /// and capturing while its window is in the background, so the PC stays
    /// usable while it runs. Sound is muted and the intro video skipped.
    /// </summary>
    public static class UiTour
    {
        static bool? _active;

        public static bool Active
        {
            get
            {
                if (!_active.HasValue)
                {
                    try { _active = File.Exists(Path.Combine(BepInEx.Paths.ConfigPath, "cupheads-uitour.flag")); }
                    catch { _active = false; }
                }
                return _active.Value;
            }
        }

        static bool? _pressTest;

        /// <summary>
        /// cupheads-presstest.flag: on screen, Player One joins on the keyboard
        /// by itself, then the game asks for a press on a spare controller and
        /// reports whether it was handed over.
        /// </summary>
        public static bool PressTestActive
        {
            get
            {
                if (!_pressTest.HasValue)
                {
                    try { _pressTest = File.Exists(Path.Combine(BepInEx.Paths.ConfigPath, "cupheads-presstest.flag")); }
                    catch { _pressTest = false; }
                }
                return _pressTest.Value;
            }
        }

        /// <summary>Either developer run mode (both skip the intro video).</summary>
        public static bool AnyActive => Active || PressTestActive;

        static string OutDir => Path.Combine(BepInEx.Paths.BepInExRootPath, "CupHeadsUiTour");

        public static void MaybeBegin(MonoBehaviour host)
        {
            if (host != null && PressTestActive && !Active)
            {
                Plugin.Log.LogInfo("[PressTest] Starting the controller press test.");
                host.StartCoroutine(RunPressTest());
                return;
            }

            if (!Active || host == null)
                return;

            Application.runInBackground = true;
            AudioListener.volume = 0f;
            KeepSmallWindow();
            SceneManager.sceneLoaded += (scene, mode) => KeepSmallWindow();
            try
            {
                if (Directory.Exists(OutDir))
                    Directory.Delete(OutDir, true);
                Directory.CreateDirectory(OutDir);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[UiTour] Could not prepare " + OutDir + ": " + ex.Message);
            }

            Plugin.Log.LogInfo("[UiTour] Starting the self-driving UI tour.");
            host.StartCoroutine(Run());
        }

        static IEnumerator Run()
        {
            int shot = 0;

            // ── Title screen: let Player One "join" without a key press ──────
            yield return WaitFor(() => TryAdvanceTitle(), 90f, "title screen");
            yield return WaitFor(() => SceneManager.GetActiveScene().name == "scene_slot_select"
                                       && MpMenuState.ScreenInstance != null
                                       && PlayerData.Initialized, 60f, "main menu");
            yield return Pause(4f);

            yield return Shot(++shot, "main-menu");

            ShortcutsCard.Toggle();
            yield return Pause(0.8f);
            yield return Shot(++shot, "shortcuts-card");
            ShortcutsCard.Toggle();
            yield return Pause(0.6f);

            // ── Lobby, live then each phase with sample data ─────────────────
            if (SlotSelectUpdatePatch.TourOpenLobby())
            {
                yield return Pause(1.2f);
                yield return Shot(++shot, "lobby-live");

                for (int phase = 0; phase < SlotSelectUpdatePatch.TourPhaseCount; phase++)
                {
                    SlotSelectUpdatePatch.TourPreviewPhase(phase);
                    yield return Pause(0.7f);
                    yield return Shot(++shot, "lobby-" + SlotSelectUpdatePatch.TourPhaseName(phase));
                }

                SlotSelectUpdatePatch.TourCloseLobby();
                yield return Pause(0.8f);
            }
            else
            {
                Plugin.Log.LogWarning("[UiTour] Could not open the lobby.");
            }

            // ── In-game overlays with sample data ────────────────────────────
            UiPreview.SetStep(1);
            yield return Pause(1.2f);
            yield return Shot(++shot, "overlays");
            UiPreview.SetStep(2);
            yield return Pause(0.8f);
            yield return Shot(++shot, "results-card");
            UiPreview.SetStep(0);

            // ── Controller handoff (offline rules), as if a spare pad was pressed ──
            Plugin.Log.LogInfo("[UiTour] Input before handoff: " + InputDeviceSwitcher.Describe());
            if (InputDeviceSwitcher.TestHandOverFirstSpareController())
            {
                yield return Pause(1.2f);
                yield return Shot(++shot, "controller-handoff");
                Plugin.Log.LogInfo("[UiTour] Input after handoff: " + InputDeviceSwitcher.Describe());
            }
            else
            {
                Plugin.Log.LogInfo("[UiTour] No spare controller to hand over.");
            }
            Plugin.Log.LogInfo("[UiTour] Done: " + shot + " screenshot(s) in " + OutDir + ". Quitting.");
            yield return Pause(1f);
            Application.Quit();
        }

        static IEnumerator RunPressTest()
        {
            yield return WaitFor(() => TryAdvanceTitle(), 90f, "title screen");
            yield return WaitFor(() => SceneManager.GetActiveScene().name == "scene_slot_select"
                                       && MpMenuState.ScreenInstance != null, 60f, "main menu");
            yield return Pause(2f);

            var playerOne = PlayerManager.GetPlayerInput(PlayerId.PlayerOne);
            if (playerOne != null && playerOne.controllers.joystickCount > 0)
            {
                // A press during the title screen joined Player One on the
                // controller the vanilla way; the switch was never exercised.
                Plugin.Log.LogInfo("[PressTest] INVALID: Player One already had the controller before the prompt. " + InputDeviceSwitcher.Describe());
                ConnectionHUD.Show("Test not valid: the controller was pressed too early. Closing.");
                yield return Pause(4f);
                Application.Quit();
                yield break;
            }
            Plugin.Log.LogInfo("[PressTest] Waiting for a controller press. " + InputDeviceSwitcher.Describe());

            float until = Time.realtimeSinceStartup + 90f;
            float nextPrompt = 0f;
            bool passed = false;
            while (Time.realtimeSinceStartup < until)
            {
                if (playerOne != null && playerOne.controllers.joystickCount > 0)
                {
                    passed = true;
                    break;
                }
                if (Time.realtimeSinceStartup >= nextPrompt)
                {
                    nextPrompt = Time.realtimeSinceStartup + 3f;
                    ConnectionHUD.Show("PRESS A ON YOUR CONTROLLER NOW (you're on the keyboard).");
                }
                yield return null;
            }

            Plugin.Log.LogInfo("[PressTest] " + (passed ? "PASSED" : "NO PRESS SEEN") + ": " + InputDeviceSwitcher.Describe());
            yield return Pause(1f);
            try
            {
                Directory.CreateDirectory(OutDir);
                ScreenCapture.CaptureScreenshot(Path.Combine(OutDir, "press-test.png"));
            }
            catch { }
            yield return Pause(1f);
            ConnectionHUD.Show(passed ? "Controller test passed! Closing in a moment." : "No controller press seen. Closing.");
            yield return Pause(4f);
            Application.Quit();
        }

        /// <summary>
        /// A small window that the runner keeps off-screen. (Unity remembers
        /// this mode on exit; the tour runner restores the player's display
        /// settings afterwards.)
        /// </summary>
        static void KeepSmallWindow()
        {
            if (Screen.fullScreen || Screen.width != 1280 || Screen.height != 720)
                Screen.SetResolution(1280, 720, false);
        }

        static bool TryAdvanceTitle()
        {
            if (SceneManager.GetActiveScene().name == "scene_slot_select")
                return true;

            var start = UnityEngine.Object.FindObjectOfType<StartScreen>();
            if (start == null || start.state != StartScreen.State.Title)
                return false;

            // What pressing a key does here: Player One joins on the keyboard,
            // and the title screen loads the save-slot scene.
            var slots = AccessTools.Field(typeof(PlayerManager), "playerSlots").GetValue(null) as Array;
            if (slots != null && slots.Length > 0)
            {
                var slot = slots.GetValue(0);
                var joinState = AccessTools.Field(slot.GetType(), "joinState");
                joinState.SetValue(slot, Enum.Parse(joinState.FieldType, "Joined"));
                var controllerState = AccessTools.Field(slot.GetType(), "controllerState");
                controllerState.SetValue(slot, Enum.Parse(controllerState.FieldType, "NoController"));
            }
            AccessTools.Field(typeof(StartScreen), "shouldLoadSlotSelect").SetValue(start, true);
            Plugin.Log.LogInfo("[UiTour] Title screen passed.");
            return true;
        }

        static IEnumerator WaitFor(Func<bool> condition, float timeout, string what)
        {
            float until = Time.realtimeSinceStartup + timeout;
            while (Time.realtimeSinceStartup < until)
            {
                bool done;
                try { done = condition(); }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("[UiTour] Waiting for " + what + ": " + ex.Message);
                    done = false;
                }
                if (done)
                    yield break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            Plugin.Log.LogWarning("[UiTour] Timed out waiting for " + what + ".");
        }

        static IEnumerator Pause(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
        }

        static IEnumerator Shot(int index, string name)
        {
            AudioListener.volume = 0f;
            KeepSmallWindow();
            string path = Path.Combine(OutDir, index.ToString("00") + "-" + name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            // The capture is written at the end of a later frame.
            yield return new WaitForSecondsRealtime(0.5f);
            Plugin.Log.LogInfo("[UiTour] Shot " + Path.GetFileName(path));
        }
    }
}
