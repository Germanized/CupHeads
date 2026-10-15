using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Guest-side reconciliation for the guest's own body.
    ///
    /// The guest moves its player immediately from local input (prediction);
    /// the host re-simulates that player from the same input stream, about one
    /// round trip later. Comparing the host's state against the guest's
    /// *current* position would always show a large, fake error while moving
    /// (490 units/s × RTT), so the host acknowledges which input sequence it had
    /// applied and for how long. The guest looks up where it was at that same
    /// moment in its own history, and only the genuine divergence (knockback,
    /// platform pushes, input timing jitter) is blended out over a few steps.
    /// </summary>
    public static class OwnPlayerReconciler
    {
        const int PositionHistorySize = 160;   // ~3 s of 50 Hz fixed steps
        const int InputHistorySize = 128;      // ~4 s at the 30 Hz heartbeat
        const float DeadZone = 20f;
        const float SnapDistance = 320f;
        const float BlendPerStep = 0.2f;

        struct PositionSample
        {
            public float Time;
            public Vector2 Position;
        }

        struct InputSample
        {
            public uint Seq;
            public float Time;
        }

        static readonly PositionSample[] _positions = new PositionSample[PositionHistorySize];
        static readonly InputSample[] _inputs = new InputSample[InputHistorySize];
        static int _positionHead;
        static int _positionCount;
        static int _inputHead;
        static int _inputCount;

        static Vector2 _pending;
        static bool _snapPending;
        static bool _hasAuthorityTick;
        static uint _lastAuthorityTick;

        public static float LastErrorMagnitude { get; private set; }

        static OwnPlayerReconciler()
        {
            MultiplayerSession.OnSessionStarted += Reset;
            MultiplayerSession.OnSessionEnded += Reset;
        }

        /// <summary>Guest: an input packet with this sequence was just sent.</summary>
        public static void RecordSentInput(uint seq)
        {
            _inputs[_inputHead] = new InputSample { Seq = seq, Time = Time.time };
            _inputHead = (_inputHead + 1) % InputHistorySize;
            if (_inputCount < InputHistorySize)
                _inputCount++;
        }

        /// <summary>Guest: the local body's position after a simulation step.</summary>
        public static void RecordLocalPosition(Vector2 position)
        {
            _positions[_positionHead] = new PositionSample { Time = Time.time, Position = position };
            _positionHead = (_positionHead + 1) % PositionHistorySize;
            if (_positionCount < PositionHistorySize)
                _positionCount++;
        }

        /// <summary>Guest: the host's authoritative state for the guest's own body.</summary>
        public static void OnAuthoritySnapshot(PlayerStatePacket pkt)
        {
            if (_hasAuthorityTick && !NetTick.IsNewer(pkt.Tick, _lastAuthorityTick))
                return;

            _hasAuthorityTick = true;
            _lastAuthorityTick = pkt.Tick;

            if (pkt.AckInputSeq == 0u || pkt.IsDead)
                return;

            float sentAt;
            if (!TryFindInputTime(pkt.AckInputSeq, out sentAt))
                return;

            Vector2 ownThen;
            if (!TryGetPositionAt(sentAt + pkt.AckAgeMs / 1000f, out ownThen))
                return;

            Vector2 error = new Vector2(pkt.PosX, pkt.PosY) - ownThen;
            float magnitude = error.magnitude;
            LastErrorMagnitude = magnitude;

            if (magnitude < DeadZone)
            {
                _pending = Vector2.zero;
                _snapPending = false;
                return;
            }

            // Replace, never accumulate: history is shifted by every applied
            // step, so this error already excludes corrections made earlier.
            _pending = error;
            _snapPending = magnitude > SnapDistance;
        }

        /// <summary>
        /// Guest: nudge the local body toward the reconciled position. Call once
        /// per fixed step. Returns the offset applied (zero when none).
        /// </summary>
        public static Vector2 Apply(Transform body)
        {
            if (body == null || _pending.sqrMagnitude < 0.01f)
                return Vector2.zero;

            Vector2 step = _snapPending ? _pending : _pending * BlendPerStep;
            _snapPending = false;
            _pending -= step;

            body.position += new Vector3(step.x, step.y, 0f);
            ShiftHistory(step);
            return step;
        }

        public static void Reset()
        {
            _positionHead = 0;
            _positionCount = 0;
            _inputHead = 0;
            _inputCount = 0;
            _pending = Vector2.zero;
            _snapPending = false;
            _hasAuthorityTick = false;
            _lastAuthorityTick = 0u;
            LastErrorMagnitude = 0f;
        }

        static bool TryFindInputTime(uint seq, out float time)
        {
            for (int i = 0; i < _inputCount; i++)
            {
                int index = (_inputHead - 1 - i + InputHistorySize) % InputHistorySize;
                if (_inputs[index].Seq == seq)
                {
                    time = _inputs[index].Time;
                    return true;
                }
            }

            time = 0f;
            return false;
        }

        static bool TryGetPositionAt(float time, out Vector2 position)
        {
            position = Vector2.zero;
            if (_positionCount < 2)
                return false;

            int newest = (_positionHead - 1 + PositionHistorySize) % PositionHistorySize;
            int oldest = (_positionHead - _positionCount + PositionHistorySize) % PositionHistorySize;
            if (time > _positions[newest].Time)
            {
                position = _positions[newest].Position;
                return true;
            }
            if (time < _positions[oldest].Time)
                return false;

            for (int i = 0; i < _positionCount - 1; i++)
            {
                int later = (newest - i + PositionHistorySize) % PositionHistorySize;
                int earlier = (later - 1 + PositionHistorySize) % PositionHistorySize;
                var a = _positions[earlier];
                var b = _positions[later];
                if (time < a.Time)
                    continue;

                float span = b.Time - a.Time;
                float t = span > 0.0001f ? Mathf.Clamp01((time - a.Time) / span) : 1f;
                position = Vector2.Lerp(a.Position, b.Position, t);
                return true;
            }

            return false;
        }

        static void ShiftHistory(Vector2 delta)
        {
            for (int i = 0; i < _positionCount; i++)
            {
                int index = (_positionHead - 1 - i + PositionHistorySize) % PositionHistorySize;
                _positions[index].Position += delta;
            }
        }
    }
}
