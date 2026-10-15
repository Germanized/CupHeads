using System.Collections.Generic;
using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Stores the most recently received gameplay input for network-controlled
    /// slots and turns it into Rewired-style button edges.
    ///
    /// Edges are accumulated, not recomputed per packet: when several input
    /// packets land in one frame, or a packet is lost, a press is still served
    /// (the sender's per-button press counters make taps survive loss). Each
    /// edge is served once to Update-loop readers and once to FixedUpdate-loop
    /// readers, matching how Rewired reports GetButtonDown in both loops, and
    /// expires if nobody asks for it so a stale press can never fire later.
    /// </summary>
    public static class RemoteInputDriver
    {
        const int ButtonCount = 32;

        /// <summary>No input for this long means the sender stalled; the proxy goes neutral.</summary>
        const float StallSeconds = 0.75f;

        /// <summary>How long an unread edge stays available to a loop that has not consumed it yet.</summary>
        const float EdgeLifetime = 0.2f;

        struct PendingEdge
        {
            public bool Active;
            public float ArrivedAt;
            public int UpdateFrame;
            public float FixedTime;
            public bool FixedServed;
        }

        sealed class RemoteInputState
        {
            public InputFramePacket Current;
            public bool HasData;
            public bool HasReceivedTick;
            public uint LastReceivedTick;
            public float LastReceivedAt;
            public float AppliedAtGameTime;
            public bool HasCounters;
            public readonly int[] LastCounters = new int[ButtonCount];
            public readonly PendingEdge[] Down = new PendingEdge[ButtonCount];
            public readonly PendingEdge[] Up = new PendingEdge[ButtonCount];
        }

        static readonly Dictionary<byte, RemoteInputState> _states =
            new Dictionary<byte, RemoteInputState>(2);

        static RemoteInputDriver()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static void Apply(InputFramePacket pkt)
        {
            Apply(MultiplayerSession.GetPrimaryRemoteGameplayId(), pkt);
        }

        public static void Apply(PlayerId playerId, InputFramePacket pkt)
        {
            Apply((byte)playerId, pkt);
        }

        public static void Apply(byte participantId, InputFramePacket pkt)
        {
            var state = GetOrCreateState(participantId);

            if (state.HasReceivedTick && !NetTick.IsNewer(pkt.Tick, state.LastReceivedTick))
                return;

            uint previousButtons = state.HasData ? state.Current.Buttons : 0u;
            float now = Time.realtimeSinceStartup;

            for (int b = 0; b < ButtonCount; b++)
            {
                uint mask = 1u << b;
                bool wasDown = (previousButtons & mask) != 0u;
                bool isDown = (pkt.Buttons & mask) != 0u;
                int counter = pkt.GetPressCounter(b);
                int presses = state.HasCounters ? ((counter - state.LastCounters[b]) & 3) : 0;
                state.LastCounters[b] = counter;

                // The sender counts every press (including sub-frame taps), so
                // counters alone decide down edges; a state flip without a count
                // (resuming from a muted pause while holding) is not a new press.
                // The first packet after a reset is only a baseline: a button
                // already held across a scene load must not fire as a fresh press.
                if (presses > 0)
                    Arm(ref state.Down[b], now);

                if ((wasDown && !isDown) || (presses > 0 && !isDown))
                    Arm(ref state.Up[b], now);
            }

            state.Current = pkt;
            state.HasData = true;
            state.HasCounters = true;
            state.HasReceivedTick = true;
            state.LastReceivedTick = pkt.Tick;
            state.LastReceivedAt = now;
            state.AppliedAtGameTime = Time.time;
        }

        /// <summary>
        /// Host: which input sequence is currently driving this participant and
        /// for how much game time, so the owner can reconcile against its own
        /// history at the same moment (see OwnPlayerReconciler).
        /// </summary>
        public static bool TryGetInputAck(byte participantId, out uint seq, out ushort ageMs)
        {
            seq = 0u;
            ageMs = 0;

            RemoteInputState state;
            if (!TryGetLiveState(participantId, out state) || !state.HasReceivedTick)
                return false;

            float age = Mathf.Clamp((Time.time - state.AppliedAtGameTime) * 1000f, 0f, ushort.MaxValue);
            seq = state.LastReceivedTick;
            ageMs = (ushort)age;
            return true;
        }

        public static bool HasDataFor(PlayerId playerId)
        {
            RemoteInputState state;
            return TryGetLiveState((byte)playerId, out state);
        }

        public static bool TryGetCurrent(PlayerId playerId, out InputFramePacket pkt)
        {
            return TryGetCurrent((byte)playerId, out pkt);
        }

        public static bool TryGetCurrent(byte participantId, out InputFramePacket pkt)
        {
            RemoteInputState state;
            if (TryGetLiveState(participantId, out state))
            {
                pkt = state.Current;
                return true;
            }

            pkt = default(InputFramePacket);
            return false;
        }

        public static bool WasPressedThisFrame(byte participantId, CupheadButton button)
        {
            RemoteInputState state;
            int index = (int)button;
            if (index < 0 || index >= ButtonCount || !TryGetLiveState(participantId, out state))
                return false;

            return ServeEdge(ref state.Down[index]);
        }

        public static bool WasReleasedThisFrame(byte participantId, CupheadButton button)
        {
            RemoteInputState state;
            int index = (int)button;
            if (index < 0 || index >= ButtonCount || !TryGetLiveState(participantId, out state))
                return false;

            return ServeEdge(ref state.Up[index]);
        }

        public static bool IsPressed(byte participantId, CupheadButton button)
        {
            RemoteInputState state;
            return TryGetLiveState(participantId, out state)
                && state.Current.IsPressed(button);
        }

        /// <summary>
        /// Called each FixedUpdate by the motor patches. Input staleness is now
        /// judged by wall-clock time in TryGetLiveState, so this only drops the
        /// buffered state once the sender has clearly stalled.
        /// </summary>
        public static void Tick(PlayerId playerId)
        {
            Tick((byte)playerId);
        }

        public static void Tick(byte participantId)
        {
            RemoteInputState state;
            if (!_states.TryGetValue(participantId, out state) || !state.HasData)
                return;

            if (Time.realtimeSinceStartup - state.LastReceivedAt > StallSeconds)
            {
                state.Current = default(InputFramePacket);
                state.HasData = false;
                for (int b = 0; b < ButtonCount; b++)
                {
                    state.Down[b].Active = false;
                    state.Up[b].Active = false;
                }
            }
        }

        public static void Reset()
        {
            _states.Clear();
        }

        public static void Reset(PlayerId playerId)
        {
            _states.Remove((byte)playerId);
        }

        public static void Reset(byte participantId)
        {
            _states.Remove(participantId);
        }

        static bool TryGetLiveState(byte participantId, out RemoteInputState state)
        {
            if (!_states.TryGetValue(participantId, out state) || !state.HasData)
                return false;

            return Time.realtimeSinceStartup - state.LastReceivedAt <= StallSeconds;
        }

        static RemoteInputState GetOrCreateState(byte participantId)
        {
            RemoteInputState state;
            if (!_states.TryGetValue(participantId, out state))
            {
                state = new RemoteInputState();
                _states[participantId] = state;
            }
            return state;
        }

        static void Arm(ref PendingEdge edge, float now)
        {
            edge.Active = true;
            edge.ArrivedAt = now;
            edge.UpdateFrame = -1;
            edge.FixedServed = false;
            edge.FixedTime = 0f;
        }

        static bool ServeEdge(ref PendingEdge edge)
        {
            if (!edge.Active)
                return false;

            if (Time.inFixedTimeStep)
            {
                if (edge.FixedServed)
                    return edge.FixedTime == Time.fixedTime;
                if (Time.realtimeSinceStartup - edge.ArrivedAt > EdgeLifetime)
                    return false;

                edge.FixedServed = true;
                edge.FixedTime = Time.fixedTime;
                return true;
            }

            if (edge.UpdateFrame >= 0)
                return edge.UpdateFrame == Time.frameCount;
            if (Time.realtimeSinceStartup - edge.ArrivedAt > EdgeLifetime)
                return false;

            edge.UpdateFrame = Time.frameCount;
            return true;
        }
    }
}
