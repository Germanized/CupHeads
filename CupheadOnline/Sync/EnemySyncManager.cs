using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using CupheadOnline.Net;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// HOST: broadcasts enemy / boss state at 20 Hz (60 Hz while a boss fight or
    /// recovery burst is active).
    /// CLIENT: receives EnemyStatePacket and corrects enemy positions + animation.
    ///
    /// Enemies are addressed by a deterministic hierarchy hash (EnemyRegistry),
    /// never by Unity instance IDs, which are process-local and would never match
    /// between two machines.
    ///
    /// Boss HP does not live on DamageReceiver — it lives on the level's
    /// AbstractLevelProperties object. The host broadcasts that health under a
    /// reserved ID and the client applies the difference through the game's own
    /// DealDamage path, so phase changes, the results timeline, and the win
    /// trigger all fire exactly as they would locally.
    /// </summary>
    public static class EnemySyncManager
    {
        private const int LEVEL_BOSS_HP_ID = int.MinValue;

        private static int   _broadcastCounter;
        private const  int   BROADCAST_EVERY = 3; // frames (= 20 Hz at 60 Hz)
        private static int   _recoveryBurstFrames;

        private static readonly Dictionary<System.Type, FieldInfo> _phaseFields =
            new Dictionary<System.Type, FieldInfo>(64);
        private static readonly Dictionary<int, EnemySnapshotState> _lastSent = new Dictionary<int, EnemySnapshotState>();
        private static readonly Dictionary<int, uint> _lastReceivedTicks = new Dictionary<int, uint>(128);

        // Reflection cache for the active level's boss properties object.
        private static object _bossProperties;
        private static PropertyInfo _bossCurrentHealth;
        private static FieldInfo _bossTotalHealth;
        private static MethodInfo _bossDealDamage;
        private static Level _bossPropertiesLevel;
        private static float _lastSentBossHp = float.MinValue;

        private struct EnemySnapshotState
        {
            public byte Phase;
            public Vector3 Position;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  HOST side — called every frame from Plugin.Update
        // ──────────────────────────────────────────────────────────────────────

        public static void HostTick()
        {
            if (!MultiplayerSession.IsHost || Plugin.Net == null || !Plugin.Net.IsConnected) return;

            var enemies = Object.FindObjectsOfType<DamageReceiver>();
            int enemyCount = CountEnemies(enemies);
            bool bossPriorityMode = _recoveryBurstFrames > 0 || enemyCount <= 3;

            _broadcastCounter++;
            int broadcastEvery = bossPriorityMode ? 1 : BROADCAST_EVERY;
            if (_broadcastCounter < broadcastEvery) return;
            _broadcastCounter = 0;
            if (_recoveryBurstFrames > 0)
                _recoveryBurstFrames--;

            BroadcastLevelBossHp();

            foreach (var dr in enemies)
            {
                if (dr.type != DamageReceiver.Type.Enemy) continue;
                var go = dr.gameObject;
                if (IsExcludedName(go.name)) continue;

                int stableId = EnemyRegistry.GetStableId(go);
                if (stableId == LEVEL_BOSS_HP_ID) continue;

                byte  phase = GetEnemyPhase(dr);
                int   hash  = 0;
                var   anim  = go.GetComponentInChildren<Animator>();
                if (anim != null)
                    hash = anim.GetCurrentAnimatorStateInfo(0).fullPathHash;

                bool priority = bossPriorityMode || IsBossPriority(go, phase, enemyCount);

                var pkt = new EnemyStatePacket
                {
                    InstanceId = stableId,
                    PosX       = go.transform.position.x,
                    PosY       = go.transform.position.y,
                    Hp         = -1f,
                    Phase      = phase,
                    AnimHash   = hash,
                    Tick       = MultiplayerSession.Tick,
                };

                bool reliable = priority && ShouldSendReliableDelta(pkt);
                Plugin.Net.SendEnemyState(ref pkt, reliable);
                _lastSent[pkt.InstanceId] = new EnemySnapshotState
                {
                    Phase = pkt.Phase,
                    Position = go.transform.position,
                };
            }
        }

        static void BroadcastLevelBossHp()
        {
            if (!TryEnsureBossProperties())
                return;

            float current;
            float total;
            try
            {
                current = (float)_bossCurrentHealth.GetValue(_bossProperties, null);
                total = (float)_bossTotalHealth.GetValue(_bossProperties);
            }
            catch
            {
                return;
            }

            bool changed = !Mathf.Approximately(current, _lastSentBossHp);

            var pkt = new EnemyStatePacket
            {
                InstanceId = LEVEL_BOSS_HP_ID,
                PosX       = 0f,
                PosY       = 0f,
                Hp         = current,
                Phase      = 0,
                AnimHash   = System.BitConverter.ToInt32(System.BitConverter.GetBytes(total), 0),
                Tick       = MultiplayerSession.Tick,
            };

            Plugin.Net.SendEnemyState(ref pkt, reliable: changed);
            _lastSentBossHp = current;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  CLIENT side — correct enemy state
        // ──────────────────────────────────────────────────────────────────────

        public static void OnEnemyStateReceived(EnemyStatePacket pkt)
        {
            uint lastReceivedTick;
            if (_lastReceivedTicks.TryGetValue(pkt.InstanceId, out lastReceivedTick)
             && !NetTick.IsNewer(pkt.Tick, lastReceivedTick))
            {
                return;
            }

            _lastReceivedTicks[pkt.InstanceId] = pkt.Tick;

            if (pkt.InstanceId == LEVEL_BOSS_HP_ID)
            {
                ApplyLevelBossHp(pkt);
                return;
            }

            DamageReceiver dr;
            if (!EnemyRegistry.TryGet(pkt.InstanceId, out dr))
            {
                EnemyRegistry.MarkDirty(); // rescan on the next (cooled-down) query
                if (!EnemyRegistry.TryGet(pkt.InstanceId, out dr)) return;
            }

            var go = dr.gameObject;

            // ── Position: gentle lerp to avoid visual snap ────────────────────
            var targetPos = new Vector3(pkt.PosX, pkt.PosY, go.transform.position.z);
            float distance = Vector3.Distance(go.transform.position, targetPos);
            go.transform.position = distance > 6f
                ? targetPos
                : Vector3.Lerp(go.transform.position, targetPos, 0.3f);

            // ── Animation: play the host's animator state ─────────────────────
            var anim = go.GetComponentInChildren<Animator>();
            if (anim != null && pkt.AnimHash != 0)
            {
                int localHash = anim.GetCurrentAnimatorStateInfo(0).fullPathHash;
                if (localHash != pkt.AnimHash)
                    anim.Play(pkt.AnimHash, 0, -1f);
            }
        }

        static void ApplyLevelBossHp(EnemyStatePacket pkt)
        {
            if (MultiplayerSession.IsHost)
                return;
            if (!TryEnsureBossProperties())
                return;

            float localCurrent;
            try
            {
                localCurrent = (float)_bossCurrentHealth.GetValue(_bossProperties, null);
            }
            catch
            {
                return;
            }

            float delta = localCurrent - pkt.Hp;
            if (delta <= 0.001f)
                return;

            // Route the correction through the game's own damage path so phase
            // transitions, the timeline, and the win trigger fire naturally.
            try
            {
                if (_bossDealDamage != null)
                    _bossDealDamage.Invoke(_bossProperties, new object[] { delta });
                else
                    _bossCurrentHealth.SetValue(_bossProperties, pkt.Hp, null);
            }
            catch
            {
            }
        }

        static bool TryEnsureBossProperties()
        {
            var level = Level.Current;
            if (level == null)
            {
                ResetBossPropertiesCache();
                return false;
            }

            if (_bossProperties != null && ReferenceEquals(_bossPropertiesLevel, level))
                return _bossCurrentHealth != null && _bossTotalHealth != null;

            ResetBossPropertiesCache();
            _bossPropertiesLevel = level;

            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var field in level.GetType().GetFields(bf))
            {
                object value;
                try { value = field.GetValue(level); }
                catch { continue; }

                if (value == null)
                    continue;

                var type = value.GetType();
                var currentHealth = type.GetProperty("CurrentHealth", bf);
                var totalHealth = type.GetField("TotalHealth", bf);
                if (currentHealth == null
                 || currentHealth.PropertyType != typeof(float)
                 || !currentHealth.CanRead
                 || !currentHealth.CanWrite
                 || totalHealth == null
                 || totalHealth.FieldType != typeof(float))
                {
                    continue;
                }

                _bossProperties = value;
                _bossCurrentHealth = currentHealth;
                _bossTotalHealth = totalHealth;
                _bossDealDamage = type.GetMethod("DealDamage", bf, null, new[] { typeof(float) }, null);
                return true;
            }

            return false;
        }

        static void ResetBossPropertiesCache()
        {
            _bossProperties = null;
            _bossCurrentHealth = null;
            _bossTotalHealth = null;
            _bossDealDamage = null;
            _bossPropertiesLevel = null;
            _lastSentBossHp = float.MinValue;
        }

        public static void Reset()
        {
            _broadcastCounter = 0;
            _recoveryBurstFrames = 0;
            _lastSent.Clear();
            _lastReceivedTicks.Clear();
            ResetBossPropertiesCache();
        }

        public static void TriggerRecoveryBurst(int frames = 150)
        {
            _recoveryBurstFrames = Mathf.Max(_recoveryBurstFrames, frames);
        }

        /// <summary>Reads the active level's boss health, if a boss properties object exists.</summary>
        public static bool TryGetLocalBossHp(out float currentHp)
        {
            currentHp = 0f;
            if (!TryEnsureBossProperties())
                return false;

            try
            {
                currentHp = (float)_bossCurrentHealth.GetValue(_bossProperties, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Directly overwrites local boss health (no damage events). Used by the
        /// desync sentinel for upward corrections, which DealDamage cannot do.
        /// </summary>
        public static void ForceLocalBossHp(float hp)
        {
            if (!TryEnsureBossProperties())
                return;

            try { _bossCurrentHealth.SetValue(_bossProperties, hp, null); }
            catch { }
        }

        // ── User-configurable sync exclusions ─────────────────────────────────
        // Escape hatch for objects whose spawn order is nondeterministic (their
        // stable IDs would cross-match). Configured via
        // Networking.EnemySyncExcludeNames, comma-separated name substrings.
        static string _excludeRaw;
        static string[] _excludeParsed = new string[0];

        internal static bool IsExcludedName(string objectName)
        {
            string raw = Plugin.EnemySyncExcludeNames;
            if (!ReferenceEquals(raw, _excludeRaw))
            {
                _excludeRaw = raw;
                if (string.IsNullOrEmpty(raw))
                {
                    _excludeParsed = new string[0];
                }
                else
                {
                    var parts = raw.Split(',');
                    var cleaned = new System.Collections.Generic.List<string>(parts.Length);
                    for (int i = 0; i < parts.Length; i++)
                    {
                        string trimmed = parts[i].Trim().ToLowerInvariant();
                        if (trimmed.Length > 0)
                            cleaned.Add(trimmed);
                    }
                    _excludeParsed = cleaned.ToArray();
                }
            }

            if (_excludeParsed.Length == 0 || string.IsNullOrEmpty(objectName))
                return false;

            string lower = objectName.ToLowerInvariant();
            for (int i = 0; i < _excludeParsed.Length; i++)
            {
                if (lower.Contains(_excludeParsed[i]))
                    return true;
            }

            return false;
        }

        static byte GetEnemyPhase(DamageReceiver dr)
        {
            var t = dr.GetType();
            FieldInfo fi;
            if (!_phaseFields.TryGetValue(t, out fi))
            {
                const BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
                foreach (var name in new[] { "phase", "currentPhase", "_phase", "Phase" })
                {
                    fi = t.GetField(name, bf);
                    if (fi != null && fi.FieldType == typeof(int))
                        break;
                    fi = null;
                }
                _phaseFields[t] = fi;
            }

            if (fi == null)
                return 0;

            try { return (byte)(int)fi.GetValue(dr); }
            catch { return 0; }
        }

        static int CountEnemies(DamageReceiver[] receivers)
        {
            int count = 0;
            for (int i = 0; i < receivers.Length; i++)
            {
                if (receivers[i] != null && receivers[i].type == DamageReceiver.Type.Enemy)
                    count++;
            }
            return count;
        }

        static bool ShouldSendReliableDelta(EnemyStatePacket pkt)
        {
            EnemySnapshotState previous;
            if (!_lastSent.TryGetValue(pkt.InstanceId, out previous))
                return true;

            if (previous.Phase != pkt.Phase)
                return true;

            var prevPos = previous.Position;
            float dx = prevPos.x - pkt.PosX;
            float dy = prevPos.y - pkt.PosY;
            return (dx * dx + dy * dy) >= 16f;
        }

        static bool IsBossPriority(GameObject go, byte phase, int enemyCount)
        {
            if (phase > 0)
                return true;
            if (enemyCount <= 3)
                return true;
            if (go == null)
                return false;

            string name = go.name.ToLowerInvariant();
            return name.Contains("boss")
                || name.Contains("baroness")
                || name.Contains("dragon")
                || name.Contains("robot")
                || name.Contains("saltbaker")
                || name.Contains("dice")
                || name.Contains("devil")
                || name.Contains("pirate")
                || name.Contains("train")
                || name.Contains("genie")
                || name.Contains("clown")
                || name.Contains("flower")
                || name.Contains("blimp")
                || name.Contains("bee");
        }
    }
}
