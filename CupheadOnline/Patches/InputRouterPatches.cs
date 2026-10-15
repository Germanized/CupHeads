using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CupheadOnline.Net;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    internal static class UniversalInputRouter
    {
        internal static readonly MethodInfo RewiredGetAxisMethod =
            AccessTools.Method(typeof(Rewired.Player), "GetAxis", new[] { typeof(int) });
        internal static readonly MethodInfo RewiredGetButtonMethod =
            AccessTools.Method(typeof(Rewired.Player), "GetButton", new[] { typeof(int) });
        internal static readonly MethodInfo RewiredGetButtonDownMethod =
            AccessTools.Method(typeof(Rewired.Player), "GetButtonDown", new[] { typeof(int) });
        internal static readonly MethodInfo RewiredGetButtonUpMethod =
            AccessTools.Method(typeof(Rewired.Player), "GetButtonUp", new[] { typeof(int) });

        static int _rawQueryDepth;

        internal static bool IsRawQuery => _rawQueryDepth > 0;

        internal static Rewired.Player GetActions(PlayerId playerId)
        {
            // PlayerManager's input table is not built during very early boot.
            try { return PlayerManager.GetPlayerInput(playerId); }
            catch { return null; }
        }

        internal static bool TryResolvePlayerId(object rewiredPlayer, out PlayerId playerId)
        {
            if (rewiredPlayer != null)
            {
                if (ReferenceEquals(rewiredPlayer, GetActions(PlayerId.PlayerOne)))
                {
                    playerId = PlayerId.PlayerOne;
                    return true;
                }

                if (ReferenceEquals(rewiredPlayer, GetActions(PlayerId.PlayerTwo)))
                {
                    playerId = PlayerId.PlayerTwo;
                    return true;
                }
            }

            playerId = PlayerId.None;
            return false;
        }

        internal static bool TryGetRemoteButton(PlayerId playerId, int actionId, bool down, bool up, out bool value)
        {
            value = false;
            if (!IsValidButton(actionId))
                return true;

            var button = (CupheadButton)actionId;
            var participantId = (byte)playerId;

            if (down)
                value = RemoteInputDriver.WasPressedThisFrame(participantId, button);
            else if (up)
                value = RemoteInputDriver.WasReleasedThisFrame(participantId, button);
            else
                value = RemoteInputDriver.IsPressed(participantId, button);

            return true;
        }

        internal static bool TryGetRemoteAxis(PlayerId playerId, int actionId, out float value)
        {
            value = 0f;
            InputFramePacket input;
            if (!RemoteInputDriver.TryGetCurrent(playerId, out input))
                return true;

            // MoveHorizontal/MoveVertical are actions 0/1; the menu navigation
            // axes MenuHorizontal/MenuVertical (22/23) mirror the same stick.
            switch (actionId)
            {
                case (int)CupheadButton.MoveHorizontal:
                case (int)CupheadButton.MenuHorizontal:
                    value = input.AxisX;
                    break;
                case (int)CupheadButton.MoveVertical:
                case (int)CupheadButton.MenuVertical:
                    value = input.AxisY;
                    break;
            }
            return true;
        }

        enum ButtonQuery
        {
            Held,
            Down,
            Up,
        }

        /// <summary>Local button state merged across both Rewired players (keyboard + any pad).</summary>
        internal static bool TryGetLocalButton(int actionId, bool down, bool up, out bool value)
        {
            return TryGetLocalButton(PlayerId.None, actionId, down, up, out value);
        }

        internal static bool TryGetLocalButton(PlayerId playerId, int actionId, bool down, bool up, out bool value)
        {
            value = false;
            if (!IsValidButton(actionId))
                return true;

            var query = down ? ButtonQuery.Down : up ? ButtonQuery.Up : ButtonQuery.Held;
            if (playerId != PlayerId.None)
            {
                value = RawButton(GetActions(playerId), query, actionId);
                return true;
            }

            value = RawButton(GetActions(PlayerId.PlayerOne), query, actionId)
                 || RawButton(GetActions(PlayerId.PlayerTwo), query, actionId);
            return true;
        }

        internal static bool TryGetLocalAxis(int actionId, out float value)
        {
            return TryGetLocalAxis(PlayerId.None, actionId, out value);
        }

        internal static bool TryGetLocalAxis(PlayerId playerId, int actionId, out float value)
        {
            if (playerId != PlayerId.None)
            {
                value = RawAxis(GetActions(playerId), actionId);
                return true;
            }

            float one = RawAxis(GetActions(PlayerId.PlayerOne), actionId);
            float two = RawAxis(GetActions(PlayerId.PlayerTwo), actionId);
            value = Mathf.Abs(two) > Mathf.Abs(one) ? two : one;
            return true;
        }

        // Raw queries bypass our own Rewired postfixes (IsRawQuery) so the
        // local device state is read, not the routed/remote value.
        static bool RawButton(Rewired.Player player, ButtonQuery query, int actionId)
        {
            if (player == null)
                return false;

            _rawQueryDepth++;
            try
            {
                switch (query)
                {
                    case ButtonQuery.Down: return player.GetButtonDown(actionId);
                    case ButtonQuery.Up: return player.GetButtonUp(actionId);
                    default: return player.GetButton(actionId);
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                _rawQueryDepth--;
            }
        }

        static float RawAxis(Rewired.Player player, int actionId)
        {
            if (player == null)
                return 0f;

            _rawQueryDepth++;
            try
            {
                return player.GetAxis(actionId);
            }
            catch
            {
                return 0f;
            }
            finally
            {
                _rawQueryDepth--;
            }
        }

        static bool IsValidButton(int actionId)
        {
            return actionId >= 0 && actionId < 32;
        }
    }

    /// <summary>
    /// Samples local input into InputFramePackets with a monotonically increasing
    /// sequence and per-button wrapping press counters. One instance per input
    /// source (the network pump and the same-PC dev simulator each own one).
    /// </summary>
    internal sealed class InputFrameBuilder
    {
        static readonly CupheadButton[] SampledButtons =
        {
            CupheadButton.Jump,
            CupheadButton.Shoot,
            CupheadButton.Super,
            CupheadButton.SwitchWeapon,
            CupheadButton.Lock,
            CupheadButton.Dash,
            CupheadButton.Pause,
            CupheadButton.NextPage,
            CupheadButton.PreviousPage,
            CupheadButton.Accept,
            CupheadButton.Cancel,
            CupheadButton.EquipMenu,
            CupheadButton.MenuUp,
            CupheadButton.MenuLeft,
            CupheadButton.MenuDown,
            CupheadButton.MenuRight,
            CupheadButton.Swap,
        };

        readonly int[] _counters = new int[32];
        uint _lastButtons;
        uint _nextTick = 1;

        /// <param name="muted">
        /// Report neutral input (used while paused). Held state is still tracked
        /// so a button held through the pause is not reported as a new press
        /// when input resumes.
        /// </param>
        internal InputFramePacket Build(PlayerId source, bool muted)
        {
            uint buttons = 0u;
            uint pressedNow = 0u;

            for (int i = 0; i < SampledButtons.Length; i++)
            {
                int action = (int)SampledButtons[i];
                uint mask = 1u << action;

                bool held;
                bool down;
                UniversalInputRouter.TryGetLocalButton(source, action, false, false, out held);
                UniversalInputRouter.TryGetLocalButton(source, action, true, false, out down);

                // A tap that starts and ends inside one frame still reports
                // GetButtonDown, so count it even if GetButton is already false.
                if (held || down)
                    buttons |= mask;
                if (down || (held && (_lastButtons & mask) == 0u))
                    pressedNow |= mask;
            }

            _lastButtons = buttons;
            if (muted)
            {
                buttons = 0u;
                pressedNow = 0u;
            }

            ulong packedCounters = 0UL;
            for (int b = 0; b < 32; b++)
            {
                if ((pressedNow & (1u << b)) != 0u)
                    _counters[b] = (_counters[b] + 1) & 3;
                packedCounters |= (ulong)_counters[b] << (b * 2);
            }

            float axisX = 0f;
            float axisY = 0f;
            if (!muted)
            {
                UniversalInputRouter.TryGetLocalAxis(source, (int)CupheadButton.MoveHorizontal, out axisX);
                UniversalInputRouter.TryGetLocalAxis(source, (int)CupheadButton.MoveVertical, out axisY);
            }

            return new InputFramePacket
            {
                AxisX = axisX,
                AxisY = axisY,
                Buttons = buttons,
                PressCounters = packedCounters,
                Tick = NextTick(),
            };
        }

        internal void Reset()
        {
            for (int i = 0; i < _counters.Length; i++)
                _counters[i] = 0;
            _lastButtons = 0u;
            _nextTick = 1;
        }

        uint NextTick()
        {
            unchecked
            {
                uint next = _nextTick++;
                if (next == 0u)
                {
                    _nextTick = 2;
                    next = 1;
                }
                return next;
            }
        }
    }

    /// <summary>
    /// Streams local input to the peer: immediately whenever it changes, plus a
    /// heartbeat so the receiver's stall detector and loss recovery stay fed.
    /// Input is muted while the local game is paused inside a level, so pause-
    /// menu navigation never makes the remote copy of this player jump or shoot.
    /// </summary>
    internal static class ClientInputFramePump
    {
        const float HeartbeatInterval = 1f / 30f;
        const float AxisChangeThreshold = 0.05f;

        static readonly InputFrameBuilder Builder = new InputFrameBuilder();
        static InputFramePacket _lastSent;
        static bool _hasSent;
        static float _lastSentAt;

        static ClientInputFramePump()
        {
            MultiplayerSession.OnSessionStarted += Reset;
            MultiplayerSession.OnSessionEnded += Reset;
        }

        internal static void Update()
        {
            if (!MultiplayerSession.IsActive || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            var packet = Builder.Build(PlayerId.None, ShouldMuteGameplayInput());

            float now = Time.realtimeSinceStartup;
            bool changed = !_hasSent
                || packet.Buttons != _lastSent.Buttons
                || packet.PressCounters != _lastSent.PressCounters
                || Mathf.Abs(packet.AxisX - _lastSent.AxisX) > AxisChangeThreshold
                || Mathf.Abs(packet.AxisY - _lastSent.AxisY) > AxisChangeThreshold;

            if (!changed && now - _lastSentAt < HeartbeatInterval)
                return;

            Plugin.Net.SendInputFrame(ref packet);
            _lastSent = packet;
            _hasSent = true;
            _lastSentAt = now;

            if (MultiplayerSession.IsClient)
                OwnPlayerReconciler.RecordSentInput(packet.Tick);
        }

        static bool ShouldMuteGameplayInput()
        {
            try
            {
                return Level.Current != null && PauseManager.state == PauseManager.State.Paused;
            }
            catch
            {
                return false;
            }
        }

        static void Reset()
        {
            Builder.Reset();
            _hasSent = false;
            _lastSentAt = 0f;
        }
    }

    [HarmonyPatch]
    public static class RewiredPlayerGetAxisPatch
    {
        static MethodBase TargetMethod()
        {
            return UniversalInputRouter.RewiredGetAxisMethod;
        }

        static void Postfix(object __instance, int actionId, ref float __result)
        {
            if (UniversalInputRouter.IsRawQuery || !MultiplayerSession.IsActive)
                return;

            PlayerId owner;
            if (!UniversalInputRouter.TryResolvePlayerId(__instance, out owner))
                return;

            float value;
            if (MultiplayerSession.IsNetworkControlledPlayer(owner))
            {
                if (UniversalInputRouter.TryGetRemoteAxis(owner, actionId, out value))
                    __result = value;
                return;
            }

            if (MultiplayerSession.IsLocalPlayer(owner)
             && (LocalDevSession.IsActive
                    ? UniversalInputRouter.TryGetLocalAxis(owner, actionId, out value)
                    : UniversalInputRouter.TryGetLocalAxis(actionId, out value)))
            {
                __result = value;
            }
        }
    }

    [HarmonyPatch]
    public static class RewiredPlayerGetButtonPatch
    {
        static MethodBase TargetMethod()
        {
            return UniversalInputRouter.RewiredGetButtonMethod;
        }

        static void Postfix(object __instance, int actionId, ref bool __result)
        {
            RouteButton(__instance, actionId, false, false, ref __result);
        }

        internal static void RouteButton(object rewiredPlayer, int actionId, bool down, bool up, ref bool result)
        {
            if (UniversalInputRouter.IsRawQuery || !MultiplayerSession.IsActive)
                return;

            PlayerId owner;
            if (!UniversalInputRouter.TryResolvePlayerId(rewiredPlayer, out owner))
                return;

            bool value;
            if (MultiplayerSession.IsNetworkControlledPlayer(owner))
            {
                if (UniversalInputRouter.TryGetRemoteButton(owner, actionId, down, up, out value))
                    result = value;
                return;
            }

            if (MultiplayerSession.IsLocalPlayer(owner)
             && (LocalDevSession.IsActive
                    ? UniversalInputRouter.TryGetLocalButton(owner, actionId, down, up, out value)
                    : UniversalInputRouter.TryGetLocalButton(actionId, down, up, out value)))
            {
                result = result || value;
            }
        }
    }

    [HarmonyPatch]
    public static class RewiredPlayerGetButtonDownPatch
    {
        static MethodBase TargetMethod()
        {
            return UniversalInputRouter.RewiredGetButtonDownMethod;
        }

        static void Postfix(object __instance, int actionId, ref bool __result)
        {
            RewiredPlayerGetButtonPatch.RouteButton(__instance, actionId, true, false, ref __result);
        }
    }

    [HarmonyPatch]
    public static class RewiredPlayerGetButtonUpPatch
    {
        static MethodBase TargetMethod()
        {
            return UniversalInputRouter.RewiredGetButtonUpMethod;
        }

        static void Postfix(object __instance, int actionId, ref bool __result)
        {
            RewiredPlayerGetButtonPatch.RouteButton(__instance, actionId, false, true, ref __result);
        }
    }

    [HarmonyPatch(typeof(CupheadInput), nameof(CupheadInput.InputDisplayForButton))]
    public static class CupheadInputDisplayForButtonPatch
    {
        static bool _insideFallback;

        static void Postfix(CupheadButton button, int rewiredPlayerId, ref Localization.Translation __result)
        {
            if (_insideFallback || HasText(__result))
                return;

            if (rewiredPlayerId != 0)
            {
                _insideFallback = true;
                try
                {
                    __result = CupheadInput.InputDisplayForButton(button, 0);
                }
                catch
                {
                }
                finally
                {
                    _insideFallback = false;
                }

                if (HasText(__result))
                    return;
            }

            __result = new Localization.Translation { text = FallbackText(button) };
        }

        static bool HasText(Localization.Translation translation)
        {
            return !string.IsNullOrEmpty(translation.text);
        }

        static string FallbackText(CupheadButton button)
        {
            switch (button)
            {
                case CupheadButton.Accept: return "Z";
                case CupheadButton.Cancel: return "ESC";
                case CupheadButton.EquipMenu: return "SHIFT";
                case CupheadButton.Pause: return "ESC";
                case CupheadButton.MenuUp: return "UP";
                case CupheadButton.MenuDown: return "DOWN";
                case CupheadButton.MenuLeft: return "LEFT";
                case CupheadButton.MenuRight: return "RIGHT";
                case CupheadButton.Jump: return "Z";
                case CupheadButton.Dash: return "SHIFT";
                case CupheadButton.Shoot: return "X";
                case CupheadButton.Super: return "V";
                default: return "Z";
            }
        }
    }
}
