using System.IO;

namespace CupheadOnline.Net
{
    public interface IPacket
    {
        void Write(BinaryWriter w);
        void Read(BinaryReader r);
    }

    /// <summary>
    /// Wire-format identity. PROTOCOL is bumped whenever any packet struct or
    /// packet meaning changes; peers whose PROTOCOL differs refuse to connect
    /// instead of silently misreading each other's traffic.
    /// </summary>
    public static class NetProtocol
    {
        public const byte PROTOCOL = 4;
    }

    public enum PacketType : byte
    {
        PlayerState = 0,
        InputFrame = 1,
        WeaponEvent = 2,
        DamageEvent = 3,
        EnemyState = 4,
        SceneChange = 5,
        LobbySync = 6,
        Ping = 7,
        Pong = 8,
        SessionStart = 9,
        Disconnect = 10,
        Hello = 11,
        Welcome = 12,
        Ready = 13,
        MenuSceneChange = 14,
        SaveSlotSync = 15,
        SaveProfile = 16,
        SessionSnapshot = 17,
        SessionSignal = 18,
        PlayerStatus = 19,
        ReviveRequest = 20,
        ReviveGrant = 21,
        VersionReject = 22,
        SceneReady = 23,
        CommMessage = 24,
        StatsReport = 25,
        StateHash = 26,
        LevelSync = 27,
        SaveMirrorChunk = 28,
        SceneRequest = 29,
    }

    public enum LevelSyncKind : byte
    {
        None = 0,
        /// <summary>Host → guest: both peers are loaded, start the fight now.</summary>
        Go = 1,
        /// <summary>Host → guest: the host's copy of the level was won.</summary>
        Won = 2,
        /// <summary>Host → guest: Dice Palace roll (LevelEnum = roll, Tick = turn).</summary>
        DiceRoll = 3,
        /// <summary>Host → guest: the map equip menu opened (LevelEnum = 1) or closed (0).</summary>
        EquipMenu = 4,
    }

    public enum SceneRequestKind : byte
    {
        None = 0,
        LoadLevel = 1,
        LoadScene = 2,
        Retry = 3,
        ExitToMap = 4,
    }

    /// <summary>Named weapon-event kinds (previously magic bytes 0–4).</summary>
    public enum WeaponEventType : byte
    {
        Fire = 0,
        Ex = 1,
        Super = 2,
        Parry = 3,
        Switch = 4,
    }

    public enum SessionSignalKind : byte
    {
        None = 0,
        GuestReady = 1,
        GuestUnready = 2,
        RequestRecovery = 3,
        PauseOn = 4,
        PauseOff = 5,
    }

    public struct PlayerStatePacket : IPacket
    {
        public byte PlayerId;
        public float PosX;
        public float PosY;
        public sbyte LookX;
        public sbyte LookY;
        public byte Flags;
        public byte AnimState;
        public uint Tick;
        /// <summary>
        /// Host → guest, for the guest's own body: the newest input sequence the
        /// host had applied when this state was taken (0 = not input-driven).
        /// </summary>
        public uint AckInputSeq;
        /// <summary>Game-time milliseconds the host had been running AckInputSeq for.</summary>
        public ushort AckAgeMs;

        public bool Grounded => (Flags & 1) != 0;
        public bool Dashing => (Flags & 2) != 0;
        public bool Ducking => (Flags & 4) != 0;
        public bool GravReversed => (Flags & 8) != 0;
        public bool IsHit => (Flags & 16) != 0;
        public bool IsSuper => (Flags & 32) != 0;
        public bool IsDead => (Flags & 64) != 0;

        public void Write(BinaryWriter w)
        {
            w.Write(PlayerId);
            w.Write(PosX);
            w.Write(PosY);
            w.Write(LookX);
            w.Write(LookY);
            w.Write(Flags);
            w.Write(AnimState);
            w.Write(Tick);
            w.Write(AckInputSeq);
            w.Write(AckAgeMs);
        }

        public void Read(BinaryReader r)
        {
            PlayerId = r.ReadByte();
            PosX = r.ReadSingle();
            PosY = r.ReadSingle();
            LookX = r.ReadSByte();
            LookY = r.ReadSByte();
            Flags = r.ReadByte();
            AnimState = r.ReadByte();
            Tick = r.ReadUInt32();
            AckInputSeq = r.ReadUInt32();
            AckAgeMs = r.ReadUInt16();
        }
    }

    public struct InputFramePacket : IPacket
    {
        public float AxisX;
        public float AxisY;
        public uint Buttons;
        /// <summary>
        /// 2-bit wrapping press counter per button (button N lives in bits 2N..2N+1).
        /// A press that starts and ends between two packets, or whose packet is
        /// lost, still shows up as a counter change in the next packet, so taps
        /// (jump, dash, parry) are never silently dropped on a lossy link.
        /// </summary>
        public ulong PressCounters;
        public uint Tick;

        public bool IsPressed(CupheadButton btn) => (Buttons & (1u << (int)btn)) != 0;

        public int GetPressCounter(int buttonIndex) => (int)((PressCounters >> (buttonIndex * 2)) & 3UL);

        public void Write(BinaryWriter w)
        {
            w.Write(AxisX);
            w.Write(AxisY);
            w.Write(Buttons);
            w.Write(PressCounters);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            AxisX = r.ReadSingle();
            AxisY = r.ReadSingle();
            Buttons = r.ReadUInt32();
            PressCounters = r.ReadUInt64();
            Tick = r.ReadUInt32();
        }
    }

    public struct WeaponEventPacket : IPacket
    {
        public byte PlayerId;
        public byte EventType;
        public sbyte AimX;
        public sbyte AimY;
        public byte WeaponId;
        public uint Tick;

        public void Write(BinaryWriter w)
        {
            w.Write(PlayerId);
            w.Write(EventType);
            w.Write(AimX);
            w.Write(AimY);
            w.Write(WeaponId);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            PlayerId = r.ReadByte();
            EventType = r.ReadByte();
            AimX = r.ReadSByte();
            AimY = r.ReadSByte();
            WeaponId = r.ReadByte();
            Tick = r.ReadUInt32();
        }
    }

    public struct DamageEventPacket : IPacket
    {
        public byte TargetPlayerId;
        public float Damage;
        public float StoneTime;
        public byte Source;
        public uint Tick;

        public void Write(BinaryWriter w)
        {
            w.Write(TargetPlayerId);
            w.Write(Damage);
            w.Write(StoneTime);
            w.Write(Source);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            TargetPlayerId = r.ReadByte();
            Damage = r.ReadSingle();
            StoneTime = r.ReadSingle();
            Source = r.ReadByte();
            Tick = r.ReadUInt32();
        }
    }

    public struct EnemyStatePacket : IPacket
    {
        public int InstanceId;
        public float PosX;
        public float PosY;
        public float Hp;
        public byte Phase;
        public int AnimHash;
        public uint Tick;

        public void Write(BinaryWriter w)
        {
            w.Write(InstanceId);
            w.Write(PosX);
            w.Write(PosY);
            w.Write(Hp);
            w.Write(Phase);
            w.Write(AnimHash);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            InstanceId = r.ReadInt32();
            PosX = r.ReadSingle();
            PosY = r.ReadSingle();
            Hp = r.ReadSingle();
            Phase = r.ReadByte();
            AnimHash = r.ReadInt32();
            Tick = r.ReadUInt32();
        }
    }

    /// <summary>
    /// SceneLoader.Context kinds that are replicated with scene changes. Only
    /// GauntletContext exists in the game today (chess gauntlet runs).
    /// </summary>
    public static class SceneContextCodec
    {
        public const byte None = 0;
        public const byte GauntletIncomplete = 1;
        public const byte GauntletComplete = 2;

        public static byte Encode(SceneLoader.Context context)
        {
            var gauntlet = context as GauntletContext;
            if (gauntlet == null)
                return None;
            return gauntlet.complete ? GauntletComplete : GauntletIncomplete;
        }

        public static SceneLoader.Context Decode(byte kind)
        {
            switch (kind)
            {
                case GauntletIncomplete: return new GauntletContext(false);
                case GauntletComplete: return new GauntletContext(true);
                default: return null;
            }
        }
    }

    public struct SceneChangePacket : IPacket
    {
        public int LevelEnum;
        public uint RngSeed;
        public byte TransitionStart;
        public byte Icon;
        public byte ContextKind;
        /// <summary>Host's Level.CurrentMode (difficulty) for the level being loaded.</summary>
        public byte Mode;

        public void Write(BinaryWriter w)
        {
            w.Write(LevelEnum);
            w.Write(RngSeed);
            w.Write(TransitionStart);
            w.Write(Icon);
            w.Write(ContextKind);
            w.Write(Mode);
        }

        public void Read(BinaryReader r)
        {
            LevelEnum = r.ReadInt32();
            RngSeed = r.ReadUInt32();
            TransitionStart = r.ReadByte();
            Icon = r.ReadByte();
            ContextKind = r.ReadByte();
            Mode = r.ReadByte();
        }
    }

    public struct LobbySyncPacket : IPacket
    {
        public byte PlayerId;
        public byte Weapon1;
        public byte Weapon2;
        public byte Super;
        public byte Charm;
        public byte IsChalice;

        public void Write(BinaryWriter w)
        {
            w.Write(PlayerId);
            w.Write(Weapon1);
            w.Write(Weapon2);
            w.Write(Super);
            w.Write(Charm);
            w.Write(IsChalice);
        }

        public void Read(BinaryReader r)
        {
            PlayerId = r.ReadByte();
            Weapon1 = r.ReadByte();
            Weapon2 = r.ReadByte();
            Super = r.ReadByte();
            Charm = r.ReadByte();
            IsChalice = r.ReadByte();
        }
    }

    public struct SessionStartPacket : IPacket
    {
        public byte Flags;
        public int CurrentLevel;
        public ushort SaveRevision;
        public uint CurrentTick;
        public uint RngSeed;

        public bool IsInLevel => (Flags & 1) != 0;
        public bool HasTrackedSave => (Flags & 2) != 0;

        public void Write(BinaryWriter w)
        {
            w.Write(Flags);
            w.Write(CurrentLevel);
            w.Write(SaveRevision);
            w.Write(CurrentTick);
            w.Write(RngSeed);
        }

        public void Read(BinaryReader r)
        {
            Flags = r.ReadByte();
            CurrentLevel = r.ReadInt32();
            SaveRevision = r.ReadUInt16();
            CurrentTick = r.ReadUInt32();
            RngSeed = r.ReadUInt32();
        }
    }

    public struct MenuSceneChangePacket : IPacket
    {
        public int SceneEnum;
        public byte TransitionStart;
        public byte TransitionEnd;
        public byte Icon;
        public uint RngSeed;
        /// <summary>Host's SceneLoader.CurrentLevel, so retries and dice/chess/graveyard flows resolve the same level.</summary>
        public int CurrentLevel;
        public byte ContextKind;
        /// <summary>Host's Level.CurrentMode, for levels entered through a scene load (Dice Palace, chess).</summary>
        public byte Mode;

        public void Write(BinaryWriter w)
        {
            w.Write(SceneEnum);
            w.Write(TransitionStart);
            w.Write(TransitionEnd);
            w.Write(Icon);
            w.Write(RngSeed);
            w.Write(CurrentLevel);
            w.Write(ContextKind);
            w.Write(Mode);
        }

        public void Read(BinaryReader r)
        {
            SceneEnum = r.ReadInt32();
            TransitionStart = r.ReadByte();
            TransitionEnd = r.ReadByte();
            Icon = r.ReadByte();
            RngSeed = r.ReadUInt32();
            CurrentLevel = r.ReadInt32();
            ContextKind = r.ReadByte();
            Mode = r.ReadByte();
        }
    }

    /// <summary>
    /// Guest → host: the guest's game wanted to change scene (enter a level from
    /// the map, enter the shop, retry, exit to map...) but only the host may
    /// load scenes. The host validates and performs it, which then syncs back.
    /// </summary>
    public struct SceneRequestPacket : IPacket
    {
        public byte Kind;
        public int Target;
        public byte Mode;
        public byte TransitionStart;
        public byte TransitionEnd;
        public byte Icon;
        public byte ContextKind;

        public SceneRequestKind RequestKind => (SceneRequestKind)Kind;

        public void Write(BinaryWriter w)
        {
            w.Write(Kind);
            w.Write(Target);
            w.Write(Mode);
            w.Write(TransitionStart);
            w.Write(TransitionEnd);
            w.Write(Icon);
            w.Write(ContextKind);
        }

        public void Read(BinaryReader r)
        {
            Kind = r.ReadByte();
            Target = r.ReadInt32();
            Mode = r.ReadByte();
            TransitionStart = r.ReadByte();
            TransitionEnd = r.ReadByte();
            Icon = r.ReadByte();
            ContextKind = r.ReadByte();
        }
    }

    /// <summary>
    /// Host → guest: one chunk of the host's gzip-compressed save (PlayerData
    /// JSON) for the active slot. The guest plays on this mirror so coins,
    /// inventory, unlocks and NPC progress match the host exactly.
    /// </summary>
    public struct SaveMirrorChunkPacket : IPacket
    {
        public const int MaxChunkBytes = 12 * 1024;

        public ushort TransferId;
        public byte Slot;
        public ushort ChunkIndex;
        public ushort ChunkCount;
        public int TotalBytes;
        public byte[] Data;

        public void Write(BinaryWriter w)
        {
            w.Write(TransferId);
            w.Write(Slot);
            w.Write(ChunkIndex);
            w.Write(ChunkCount);
            w.Write(TotalBytes);
            int length = Data != null ? Data.Length : 0;
            w.Write((ushort)length);
            if (length > 0)
                w.Write(Data, 0, length);
        }

        public void Read(BinaryReader r)
        {
            TransferId = r.ReadUInt16();
            Slot = r.ReadByte();
            ChunkIndex = r.ReadUInt16();
            ChunkCount = r.ReadUInt16();
            TotalBytes = r.ReadInt32();
            int length = r.ReadUInt16();
            if (length > MaxChunkBytes)
                throw new IOException("Save mirror chunk too large: " + length);
            Data = r.ReadBytes(length);
            if (Data.Length != length)
                throw new EndOfStreamException("Truncated save mirror chunk.");
        }
    }

    public struct SaveSlotSyncPacket : IPacket
    {
        public byte SlotIndex;
        public byte Flags;
        public ushort SaveRevision;
        public int CurrentMapScene;

        public bool IsEmpty => (Flags & 1) != 0;
        public bool Player1IsMugman => (Flags & 2) != 0;

        public void Write(BinaryWriter w)
        {
            w.Write(SlotIndex);
            w.Write(Flags);
            w.Write(SaveRevision);
            w.Write(CurrentMapScene);
        }

        public void Read(BinaryReader r)
        {
            SlotIndex = r.ReadByte();
            Flags = r.ReadByte();
            SaveRevision = r.ReadUInt16();
            CurrentMapScene = r.ReadInt32();
        }
    }

    public struct SaveProfilePacket : IPacket
    {
        public byte SlotIndex;
        public byte Flags;
        public int CurrentMapScene;
        public float CompletionPct;
        public float CompletionPctDlc;
        public ushort Coins;
        public byte Weapon1;
        public byte Weapon2;
        public byte Super;
        public byte Charm;

        public bool IsEmpty => (Flags & 1) != 0;
        public bool DlcEnabled => (Flags & 2) != 0;
        public bool Player1IsMugman => (Flags & 4) != 0;

        public void Write(BinaryWriter w)
        {
            w.Write(SlotIndex);
            w.Write(Flags);
            w.Write(CurrentMapScene);
            w.Write(CompletionPct);
            w.Write(CompletionPctDlc);
            w.Write(Coins);
            w.Write(Weapon1);
            w.Write(Weapon2);
            w.Write(Super);
            w.Write(Charm);
        }

        public void Read(BinaryReader r)
        {
            SlotIndex = r.ReadByte();
            Flags = r.ReadByte();
            CurrentMapScene = r.ReadInt32();
            CompletionPct = r.ReadSingle();
            CompletionPctDlc = r.ReadSingle();
            Coins = r.ReadUInt16();
            Weapon1 = r.ReadByte();
            Weapon2 = r.ReadByte();
            Super = r.ReadByte();
            Charm = r.ReadByte();
        }
    }

    public struct SessionSnapshotPacket : IPacket
    {
        public byte SaveSlotIndex;
        public byte Flags;
        public ushort SaveRevision;
        public int CurrentLevel;
        public int CurrentMapScene;
        public uint HostTick;
        public uint RngSeed;
        public string SceneName;

        public bool HasTrackedSave => (Flags & 1) != 0;
        public bool IsInLevel => (Flags & 2) != 0;
        public bool IsPaused => (Flags & 4) != 0;

        public void Write(BinaryWriter w)
        {
            w.Write(SaveSlotIndex);
            w.Write(Flags);
            w.Write(SaveRevision);
            w.Write(CurrentLevel);
            w.Write(CurrentMapScene);
            w.Write(HostTick);
            w.Write(RngSeed);
            w.Write(SceneName ?? string.Empty);
        }

        public void Read(BinaryReader r)
        {
            SaveSlotIndex = r.ReadByte();
            Flags = r.ReadByte();
            SaveRevision = r.ReadUInt16();
            CurrentLevel = r.ReadInt32();
            CurrentMapScene = r.ReadInt32();
            HostTick = r.ReadUInt32();
            RngSeed = r.ReadUInt32();
            SceneName = r.ReadString();
        }
    }

    public struct SessionSignalPacket : IPacket
    {
        public byte Signal;
        public ushort SaveRevision;

        public SessionSignalKind Kind => (SessionSignalKind)Signal;

        public void Write(BinaryWriter w)
        {
            w.Write(Signal);
            w.Write(SaveRevision);
        }

        public void Read(BinaryReader r)
        {
            Signal = r.ReadByte();
            SaveRevision = r.ReadUInt16();
        }
    }

    public struct PlayerStatusPacket : IPacket
    {
        public byte ParticipantId;
        public byte Health;
        public byte HealthMax;
        public byte Flags;
        public uint Tick;
        /// <summary>The owner's super meter, so EX / super presses resolve the same on both machines.</summary>
        public float SuperMeter;

        public bool IsDead => (Flags & 1) != 0;
        public bool CanDonate => (Flags & 2) != 0;
        public bool IsChalice => (Flags & 4) != 0;
        public bool IsMugman => (Flags & 8) != 0;
        /// <summary>Health/SuperMeter are meaningful (sent from inside a level).</summary>
        public bool HasLevelStats => (Flags & 16) != 0;

        public void Write(BinaryWriter w)
        {
            w.Write(ParticipantId);
            w.Write(Health);
            w.Write(HealthMax);
            w.Write(Flags);
            w.Write(Tick);
            w.Write(SuperMeter);
        }

        public void Read(BinaryReader r)
        {
            ParticipantId = r.ReadByte();
            Health = r.ReadByte();
            HealthMax = r.ReadByte();
            Flags = r.ReadByte();
            Tick = r.ReadUInt32();
            SuperMeter = r.ReadSingle();
        }
    }

    public struct ReviveRequestPacket : IPacket
    {
        public float PosX;
        public float PosY;
        public uint Tick;

        public void Write(BinaryWriter w)
        {
            w.Write(PosX);
            w.Write(PosY);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            PosX = r.ReadSingle();
            PosY = r.ReadSingle();
            Tick = r.ReadUInt32();
        }
    }

    /// <summary>Sent by a peer to refuse a connection from an incompatible build.</summary>
    public struct VersionRejectPacket : IPacket
    {
        public byte Protocol;
        public string Version;
        public string Reason;

        public void Write(BinaryWriter w)
        {
            w.Write(Protocol);
            w.Write(Version ?? string.Empty);
            w.Write(Reason ?? string.Empty);
        }

        public void Read(BinaryReader r)
        {
            Protocol = r.ReadByte();
            Version = r.ReadString();
            Reason = r.ReadString();
        }
    }

    /// <summary>Guest → host: "my copy of this level finished loading".</summary>
    public struct SceneReadyPacket : IPacket
    {
        public int LevelEnum;
        public uint Tick;

        public void Write(BinaryWriter w)
        {
            w.Write(LevelEnum);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            LevelEnum = r.ReadInt32();
            Tick = r.ReadUInt32();
        }
    }

    /// <summary>Host → guest level lifecycle signal (start gate release, knockout).</summary>
    public struct LevelSyncPacket : IPacket
    {
        public byte Kind;
        public int LevelEnum;
        public uint Tick;

        public LevelSyncKind SyncKind => (LevelSyncKind)Kind;

        public void Write(BinaryWriter w)
        {
            w.Write(Kind);
            w.Write(LevelEnum);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            Kind = r.ReadByte();
            LevelEnum = r.ReadInt32();
            Tick = r.ReadUInt32();
        }
    }

    /// <summary>Canned comm-wheel message ("wait", "go", "revive me", "nice!").</summary>
    public struct CommMessagePacket : IPacket
    {
        public byte ParticipantId;
        public byte MessageId;
        public uint Tick;

        public void Write(BinaryWriter w)
        {
            w.Write(ParticipantId);
            w.Write(MessageId);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            ParticipantId = r.ReadByte();
            MessageId = r.ReadByte();
            Tick = r.ReadUInt32();
        }
    }

    /// <summary>Per-peer level stats, exchanged when a level ends for the results card.</summary>
    public struct StatsReportPacket : IPacket
    {
        public byte ParticipantId;
        public ushort Deaths;
        public ushort Parries;
        public ushort Retries;

        public void Write(BinaryWriter w)
        {
            w.Write(ParticipantId);
            w.Write(Deaths);
            w.Write(Parries);
            w.Write(Retries);
        }

        public void Read(BinaryReader r)
        {
            ParticipantId = r.ReadByte();
            Deaths = r.ReadUInt16();
            Parries = r.ReadUInt16();
            Retries = r.ReadUInt16();
        }
    }

    /// <summary>
    /// Host → guest sanity sample: rough boss HP + player positions. The guest
    /// compares against its own simulation and requests a resync after repeated
    /// mismatches.
    /// </summary>
    public struct StateHashPacket : IPacket
    {
        public float BossHp;
        public float P1X;
        public float P1Y;
        public float P2X;
        public float P2Y;
        public byte Flags;
        public uint Tick;

        public bool HasBossHp => (Flags & 1) != 0;
        public bool HasP1 => (Flags & 2) != 0;
        public bool HasP2 => (Flags & 4) != 0;

        public void Write(BinaryWriter w)
        {
            w.Write(BossHp);
            w.Write(P1X);
            w.Write(P1Y);
            w.Write(P2X);
            w.Write(P2Y);
            w.Write(Flags);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            BossHp = r.ReadSingle();
            P1X = r.ReadSingle();
            P1Y = r.ReadSingle();
            P2X = r.ReadSingle();
            P2Y = r.ReadSingle();
            Flags = r.ReadByte();
            Tick = r.ReadUInt32();
        }
    }

    public struct ReviveGrantPacket : IPacket
    {
        public byte TargetParticipantId;
        public byte DonorParticipantId;
        public byte Flags;
        public float RevivePosX;
        public float RevivePosY;
        public uint Tick;

        public bool ApplyDonorCost => (Flags & 1) != 0;
        public bool ApplyRevive => (Flags & 2) != 0;

        public void Write(BinaryWriter w)
        {
            w.Write(TargetParticipantId);
            w.Write(DonorParticipantId);
            w.Write(Flags);
            w.Write(RevivePosX);
            w.Write(RevivePosY);
            w.Write(Tick);
        }

        public void Read(BinaryReader r)
        {
            TargetParticipantId = r.ReadByte();
            DonorParticipantId = r.ReadByte();
            Flags = r.ReadByte();
            RevivePosX = r.ReadSingle();
            RevivePosY = r.ReadSingle();
            Tick = r.ReadUInt32();
        }
    }
}
