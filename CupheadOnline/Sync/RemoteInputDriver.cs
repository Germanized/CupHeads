using System.Collections.Generic;
using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Stores the most recently received gameplay input for network-controlled
    /// slots. Today that is still one live remote slot, but the storage is keyed
    /// by PlayerId so the rest of the code does not have to stay single-remote.
    /// </summary>
    public static class RemoteInputDriver
    {
        sealed class RemoteInputState
        {
            public InputFramePacket Current;
            public InputFramePacket Previous;
            public bool HasData;
            public bool HasReceivedTick;
            public uint LastReceivedTick;
            public int StallFrames;
            public uint DownEdges;
            public uint UpEdges;
            public readonly int[] DownServedFrames = CreateServedFrameBuffer();
            public readonly int[] UpServedFrames = CreateServedFrameBuffer();
        }

        const int MAX_STALL = 45; // ~750 ms at 60 Hz before inputs are released

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

        public static void Apply(byte participantId, InputFramePacket pkt)
        {
            var state = GetOrCreateState(participantId);

            if (state.HasReceivedTick)
            {
                if (pkt.Tick == state.LastReceivedTick)
                    return;

                if (!NetTick.IsNewer(pkt.Tick, state.LastReceivedTick))
                    return;
            }

            if (state.HasData)
                state.Previous = state.Current;
            else
                state.Previous = default(InputFramePacket);

            state.Current = pkt;
            state.HasData = true;
            state.HasReceivedTick = true;
            state.LastReceivedTick = pkt.Tick;
            state.StallFrames = 0;
            RecomputeEdges(state);
        }

        public static void Apply(PlayerId playerId, InputFramePacket pkt)
        {
            Apply((byte)playerId, pkt);
        }

        public static bool HasDataFor(PlayerId playerId)
        {
            RemoteInputState state;
            return _states.TryGetValue((byte)playerId, out state) && state.HasData;
        }

        public static bool TryGetCurrent(PlayerId playerId, out InputFramePacket pkt)
        {
            return TryGetCurrent((byte)playerId, out pkt);
        }

        public static bool TryGetCurrent(byte participantId, out InputFramePacket pkt)
        {
            RemoteInputState state;
            if (_states.TryGetValue(participantId, out state) && state.HasData)
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
            if (!_states.TryGetValue(participantId, out state) || !state.HasData)
                return false;

            return ConsumeEdgeForFrame(state.DownEdges, state.DownServedFrames, button);
        }

        public static bool WasReleasedThisFrame(byte participantId, CupheadButton button)
        {
            RemoteInputState state;
            if (!_states.TryGetValue(participantId, out state) || !state.HasData)
                return false;

            return ConsumeEdgeForFrame(state.UpEdges, state.UpServedFrames, button);
        }

        public static bool IsPressed(byte participantId, CupheadButton button)
        {
            RemoteInputState state;
            return _states.TryGetValue(participantId, out state)
                && state.HasData
                && state.Current.IsPressed(button);
        }

        /// <summary>Called each FixedUpdate by PlayerMotorPatch to age the input.</summary>
        public static void Tick(PlayerId playerId)
        {
            Tick((byte)playerId);
        }

        public static void Tick(byte participantId)
        {
            RemoteInputState state;
            if (!_states.TryGetValue(participantId, out state) || !state.HasData)
                return;

            state.StallFrames++;
            if (state.StallFrames > MAX_STALL)
            {
                // Starvation: zero all inputs so the proxy player stops moving.
                state.Previous = default(InputFramePacket);
                state.Current = default(InputFramePacket);
                state.HasData = false;
                state.DownEdges = 0u;
                state.UpEdges = 0u;
                ResetServedFrames(state.DownServedFrames);
                ResetServedFrames(state.UpServedFrames);
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

        static RemoteInputState GetOrCreateState(PlayerId playerId)
        {
            return GetOrCreateState((byte)playerId);
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

        static void RecomputeEdges(RemoteInputState state)
        {
            state.DownEdges = state.Current.Buttons & ~state.Previous.Buttons;
            state.UpEdges = state.Previous.Buttons & ~state.Current.Buttons;
            ResetServedFrames(state.DownServedFrames);
            ResetServedFrames(state.UpServedFrames);
        }

        static bool ConsumeEdgeForFrame(uint edges, int[] servedFrames, CupheadButton button)
        {
            int buttonIndex = (int)button;
            if (buttonIndex < 0 || buttonIndex >= 32)
                return false;

            uint mask = 1u << buttonIndex;
            if ((edges & mask) == 0u)
                return false;

            int frame = Time.frameCount;
            if (servedFrames[buttonIndex] == frame)
                return true;
            if (servedFrames[buttonIndex] >= 0)
                return false;

            servedFrames[buttonIndex] = frame;
            return true;
        }

        static int[] CreateServedFrameBuffer()
        {
            var frames = new int[32];
            ResetServedFrames(frames);
            return frames;
        }

        static void ResetServedFrames(int[] frames)
        {
            if (frames == null)
                return;

            for (int i = 0; i < frames.Length; i++)
                frames[i] = -1;
        }
    }
}
