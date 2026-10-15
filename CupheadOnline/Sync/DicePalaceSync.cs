using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// King Dice's board: the roll is whichever face the spinning die shows at
    /// the frame it gets parried, so the two machines rolled independently and
    /// walked their markers to different spaces (and handed out different
    /// free hearts). The host's roll is now the only roll: the guest ignores its
    /// own die's result and applies the host's when it arrives.
    /// </summary>
    public static class DicePalaceSync
    {
        static bool _applyingHostRoll;
        static int _lastAppliedTurn = int.MinValue;
        static int _pendingRoll = -1;
        static int _pendingTurn;

        static bool Active =>
            MultiplayerSession.IsActive
            && !LocalDevSession.IsActive
            && Plugin.Net != null
            && Plugin.Net.IsConnected;

        /// <summary>Prefix of DicePalaceMainLevelDice.RollOne/Two/Three.</summary>
        public static bool AllowLocalRoll()
        {
            if (!Active || MultiplayerSession.IsHost || _applyingHostRoll)
                return true;

            // Guest: wait for the host's result instead.
            return false;
        }

        /// <summary>Postfix of DicePalaceMainLevelDice.RollOne/Two/Three on the host.</summary>
        public static void OnLocalRoll(DicePalaceMainLevelDice dice)
        {
            if (!Active || !MultiplayerSession.IsHost || dice == null)
                return;

            var pkt = new LevelSyncPacket
            {
                Kind = (byte)LevelSyncKind.DiceRoll,
                LevelEnum = (int)dice.roll,
                Tick = (uint)DicePalaceMainLevelGameInfo.TURN_COUNTER,
            };
            Plugin.Net.SendLevelSync(ref pkt);
            Plugin.Log.LogInfo("[DiceSync] Host rolled " + dice.roll + " (turn " + DicePalaceMainLevelGameInfo.TURN_COUNTER + ").");
        }

        /// <summary>Guest: the host's roll for a turn.</summary>
        public static void OnHostRoll(int roll, uint turn)
        {
            if (MultiplayerSession.IsHost || roll < 0 || roll > 2)
                return;

            _pendingRoll = roll;
            _pendingTurn = (int)turn;
            TryApplyPending();
        }

        public static void Update()
        {
            if (_pendingRoll >= 0)
                TryApplyPending();
        }

        public static void OnSceneChanged()
        {
            _pendingRoll = -1;
            _lastAppliedTurn = int.MinValue;
        }

        static void TryApplyPending()
        {
            if (_pendingTurn <= _lastAppliedTurn)
            {
                _pendingRoll = -1;
                return;
            }

            var dice = Object.FindObjectOfType<DicePalaceMainLevelDice>();
            if (dice == null || !dice.waitingToRoll)
                return; // the board is not asking for a roll yet

            int roll = _pendingRoll;
            _pendingRoll = -1;
            _lastAppliedTurn = _pendingTurn;

            _applyingHostRoll = true;
            try
            {
                switch ((DicePalaceMainLevelDice.Roll)roll)
                {
                    case DicePalaceMainLevelDice.Roll.One: dice.RollOne(); break;
                    case DicePalaceMainLevelDice.Roll.Two: dice.RollTwo(); break;
                    default: dice.RollThree(); break;
                }
            }
            finally
            {
                _applyingHostRoll = false;
            }

            Plugin.Log.LogInfo("[DiceSync] Applied the host's roll " + (DicePalaceMainLevelDice.Roll)roll + ".");
        }
    }
}
