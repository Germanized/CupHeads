using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using CupheadOnline.Net;
using Object = UnityEngine.Object;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// HOST: broadcasts enemy / boss state at 15 Hz (30 Hz while a boss fight or
    /// recovery burst is active), from a scene scan that is refreshed twice a
    /// second instead of every frame.
    /// CLIENT: mirrors the host's boss HP and gently corrects enemy positions and
    /// animation states.
    ///
    /// Enemies are addressed by a deterministic hierarchy hash (EnemyRegistry),
    /// never by Unity instance IDs, which are process-local.
    ///
    /// Boss HP lives on the level's AbstractLevelProperties object, not on the
    /// DamageReceiver. The guest mirrors the host's value: drops go through the
    /// game's own DealDamage path (phase changes, results timeline and the win
    /// trigger all fire naturally), and the guest's own not-yet-confirmed hits
    /// are rolled back upward without ever regressing the boss phase.
    /// </summary>
    public static class EnemySyncManager
    {
        private const int LEVEL_BOSS_HP_ID = int.MinValue;

        private const float PriorityInterval = 1f / 30f;
        private const float NormalInterval = 1f / 15f;
        private const float RescanInterval = 0.5f;
        private const float BossHpKeepAlive = 0.5f;

        // Guest-side correction tuning (world units are pixels; players run at 490/s).
        private const float PositionDeadZone = 24f;
        private const float PositionBlend = 0.2f;
        private const float PositionIgnoreBeyond = 500f;
        private const float AnimMismatchGrace = 0.35f;

        private static float _nextBroadcastAt;
        private static float _nextRescanAt;
        private static int _recoveryBurstFrames;

        private static readonly List<DamageReceiver> _hostReceivers = new List<DamageReceiver>(64);
        private static readonly Dictionary<int, Animator> _animators = new Dictionary<int, Animator>(64);
        private static readonly Dictionary<Type, FieldInfo> _phaseFields =
            new Dictionary<Type, FieldInfo>(64);
        private static readonly Dictionary<int, EnemySnapshotState> _lastSent = new Dictionary<int, EnemySnapshotState>();
        private static readonly Dictionary<int, uint> _lastReceivedTicks = new Dictionary<int, uint>(128);
        private static readonly Dictionary<int, AnimMismatch> _animMismatches = new Dictionary<int, AnimMismatch>(64);
        private static readonly HashSet<int> _identityWarnings = new HashSet<int>();

        // Reflection cache for the active level's boss properties object.
        private static object _bossProperties;
        private static PropertyInfo _bossCurrentHealth;
        private static FieldInfo _bossTotalHealth;
        private static MethodInfo _bossDealDamage;
        private static FieldInfo _bossStates;
        private static FieldInfo _bossStateIndex;
        private static Level _bossPropertiesLevel;
        private static float _nextBossSearchAt;
        private static float _lastSentBossHp = float.MinValue;
        private static float _lastBossHpSentAt = -10f;

        private struct EnemySnapshotState
        {
            public byte Phase;
            public Vector3 Position;
        }

        private struct AnimMismatch
        {
            public int HostHash;
            public float Since;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  HOST side — called every frame from Plugin.Update
        // ──────────────────────────────────────────────────────────────────────

        public static void HostTick()
        {
            if (!MultiplayerSession.IsHost || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;
            if (Level.Current == null)
                return;

            float now = Time.unscaledTime;
            if (now >= _nextRescanAt)
            {
                _nextRescanAt = now + RescanInterval;
                RescanHostReceivers();
            }

            int enemyCount = _hostReceivers.Count;
            bool bossPriorityMode = _recoveryBurstFrames > 0 || enemyCount <= 3;
            if (now < _nextBroadcastAt)
                return;

            _nextBroadcastAt = now + (bossPriorityMode ? PriorityInterval : NormalInterval);
            if (_recoveryBurstFrames > 0)
                _recoveryBurstFrames--;

            BroadcastLevelBossHp(now);

            for (int i = 0; i < _hostReceivers.Count; i++)
            {
                var dr = _hostReceivers[i];
                if (dr == null)
                    continue;

                var go = dr.gameObject;
                if (go == null || !go.activeInHierarchy)
                    continue;

                int stableId = EnemyRegistry.GetStableId(go);
                if (stableId == LEVEL_BOSS_HP_ID)
                    continue;

                byte phase = GetEnemyPhase(dr);
                int hash = 0;
                var anim = GetAnimator(dr);
                if (anim != null && anim.isActiveAndEnabled)
                    hash = anim.GetCurrentAnimatorStateInfo(0).fullPathHash;

                bool priority = bossPriorityMode || IsBossPriority(go, phase, enemyCount);
                var position = go.transform.position;

                var pkt = new EnemyStatePacket
                {
                    InstanceId = stableId,
                    PosX       = position.x,
                    PosY       = position.y,
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
                    Position = position,
                };
            }
        }

        static void RescanHostReceivers()
        {
            _hostReceivers.Clear();
            var receivers = Object.FindObjectsOfType<DamageReceiver>();
            for (int i = 0; i < receivers.Length; i++)
            {
                var dr = receivers[i];
                if (dr == null || dr.type != DamageReceiver.Type.Enemy)
                    continue;
                if (IsExcludedName(dr.gameObject.name))
                    continue;
                _hostReceivers.Add(dr);
            }
        }

        static Animator GetAnimator(DamageReceiver dr)
        {
            int key = dr.GetInstanceID();
            Animator anim;
            if (_animators.TryGetValue(key, out anim) && anim != null)
                return anim;

            anim = dr.GetComponentInChildren<Animator>();
            _animators[key] = anim;
            return anim;
        }

        static void BroadcastLevelBossHp(float now)
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
            if (!changed && now - _lastBossHpSentAt < BossHpKeepAlive)
                return;

            var pkt = new EnemyStatePacket
            {
                InstanceId = LEVEL_BOSS_HP_ID,
                PosX       = 0f,
                PosY       = 0f,
                Hp         = current,
                Phase      = 0,
                AnimHash   = BitConverter.ToInt32(BitConverter.GetBytes(total), 0),
                Tick       = MultiplayerSession.Tick,
            };

            Plugin.Net.SendEnemyState(ref pkt, reliable: changed);
            _lastSentBossHp = current;
            _lastBossHpSentAt = now;
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
            if (go == null || !go.activeInHierarchy)
                return;

            // ── Position: gentle correction toward the host ───────────────────
            var current = go.transform.position;
            var targetPos = new Vector3(pkt.PosX, pkt.PosY, current.z);
            float distance = Vector2.Distance(current, targetPos);
            if (distance > PositionIgnoreBeyond)
            {
                // Almost certainly two different objects that share a hierarchy
                // hash (spawn order differed); moving it would teleport the wrong
                // enemy across the screen.
                if (_identityWarnings.Add(pkt.InstanceId))
                    Plugin.LogVerbose("[EnemySync] Ignored far correction for " + go.name + " (" + distance.ToString("0") + " units).");
            }
            else if (distance > PositionDeadZone)
            {
                go.transform.position = Vector3.Lerp(current, targetPos, PositionBlend);
            }

            // ── Animation: adopt the host's state only once it clearly differs ─
            ApplyAnimation(pkt, dr);
        }

        static void ApplyAnimation(EnemyStatePacket pkt, DamageReceiver dr)
        {
            if (pkt.AnimHash == 0)
                return;

            var anim = GetAnimator(dr);
            if (anim == null || !anim.isActiveAndEnabled)
                return;

            int localHash = anim.GetCurrentAnimatorStateInfo(0).fullPathHash;
            if (localHash == pkt.AnimHash || anim.IsInTransition(0))
            {
                _animMismatches.Remove(pkt.InstanceId);
                return;
            }

            // Normal transitions differ for a few frames between machines;
            // forcing them would replay animation events (extra projectiles).
            float now = Time.unscaledTime;
            AnimMismatch mismatch;
            if (!_animMismatches.TryGetValue(pkt.InstanceId, out mismatch) || mismatch.HostHash != pkt.AnimHash)
            {
                _animMismatches[pkt.InstanceId] = new AnimMismatch { HostHash = pkt.AnimHash, Since = now };
                return;
            }

            if (now - mismatch.Since < AnimMismatchGrace)
                return;

            _animMismatches.Remove(pkt.InstanceId);
            if (anim.HasState(0, pkt.AnimHash))
                anim.Play(pkt.AnimHash, 0, float.NegativeInfinity);
        }

        static void ApplyLevelBossHp(EnemyStatePacket pkt)
        {
            if (MultiplayerSession.IsHost)
                return;
            if (!TryEnsureBossProperties())
                return;

            float localCurrent;
            float total;
            try
            {
                localCurrent = (float)_bossCurrentHealth.GetValue(_bossProperties, null);
                total = (float)_bossTotalHealth.GetValue(_bossProperties);
            }
            catch
            {
                return;
            }

            if (localCurrent <= 0f)
                return;

            float delta = localCurrent - pkt.Hp;
            if (delta > 0.001f)
            {
                DealBossDamage(delta);
                return;
            }

            // Our own shots landed locally before the host confirmed them:
            // follow the host's value back up (never across a phase line).
            float tolerance = Mathf.Max(1f, total * 0.004f);
            if (-delta > tolerance)
                RaiseBossHpPhaseSafe(pkt.Hp);
        }

        /// <summary>Guest: the host's copy of the boss is dead; finish ours the native way.</summary>
        public static bool TryForceBossDefeat()
        {
            if (!TryEnsureBossProperties())
                return false;

            float current;
            try { current = (float)_bossCurrentHealth.GetValue(_bossProperties, null); }
            catch { return false; }

            if (current <= 0f)
                return false;

            return DealBossDamage(current + 1f);
        }

        static bool DealBossDamage(float amount)
        {
            try
            {
                if (_bossDealDamage != null)
                {
                    _bossDealDamage.Invoke(_bossProperties, new object[] { amount });
                    return true;
                }

                float current = (float)_bossCurrentHealth.GetValue(_bossProperties, null);
                _bossCurrentHealth.SetValue(_bossProperties, Mathf.Max(0f, current - amount), null);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogVerbose("[EnemySync] Boss damage sync failed: " + (ex.InnerException ?? ex).Message);
                return false;
            }
        }

        /// <summary>
        /// Raises boss HP without events, clamped so the HP ratio stays below the
        /// current phase's trigger. DealDamage recomputes the phase index from
        /// the ratio, so crossing back above a trigger would regress the phase.
        /// </summary>
        static void RaiseBossHpPhaseSafe(float target)
        {
            try
            {
                float current = (float)_bossCurrentHealth.GetValue(_bossProperties, null);
                float total = (float)_bossTotalHealth.GetValue(_bossProperties);
                if (target <= current || total <= 0f)
                    return;

                float ceiling = total;
                var states = _bossStates != null ? _bossStates.GetValue(_bossProperties) as Array : null;
                if (states != null && _bossStateIndex != null)
                {
                    int index = (int)_bossStateIndex.GetValue(_bossProperties);
                    if (index > 0 && index < states.Length)
                    {
                        var state = states.GetValue(index);
                        var trigger = state != null ? state.GetType().GetField("healthTrigger") : null;
                        if (trigger != null)
                            ceiling = (float)trigger.GetValue(state) * total - 0.01f;
                    }
                }

                float raised = Mathf.Min(target, ceiling);
                if (raised <= current)
                    return;

                _bossCurrentHealth.SetValue(_bossProperties, raised, null);
                CompensateTimeline(raised - current);
            }
            catch (Exception ex)
            {
                Plugin.LogVerbose("[EnemySync] Boss HP raise failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Levels feed properties.OnBossDamaged into the death-card timeline. HP
        /// we gave back is damage that will be dealt again once the host
        /// confirms it, so take it off the timeline to avoid counting it twice.
        /// </summary>
        static void CompensateTimeline(float restored)
        {
            var level = Level.Current;
            if (level == null || level.timeline == null || restored <= 0f)
                return;

            try
            {
                var field = _bossProperties.GetType().GetField("OnBossDamaged", BindingFlags.Instance | BindingFlags.NonPublic)
                         ?? FindFieldInHierarchy(_bossProperties.GetType(), "OnBossDamaged");
                var handler = field != null ? field.GetValue(_bossProperties) as Delegate : null;
                if (handler == null)
                    return;

                foreach (var target in handler.GetInvocationList())
                {
                    if (ReferenceEquals(target.Target, level.timeline))
                    {
                        level.timeline.DealDamage(-restored);
                        return;
                    }
                }
            }
            catch
            {
            }
        }

        static FieldInfo FindFieldInHierarchy(Type type, string name)
        {
            const BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, bf);
                if (field != null)
                    return field;
            }
            return null;
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

            // Properties are assigned during the level's own startup, so a miss
            // is retried, but at most once a second (platforming levels never
            // have one and should not pay for a reflection scan every frame).
            if (ReferenceEquals(_bossPropertiesLevel, level) && Time.unscaledTime < _nextBossSearchAt)
                return false;

            ResetBossPropertiesCache();
            _bossPropertiesLevel = level;
            _nextBossSearchAt = Time.unscaledTime + 1f;

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
                _bossStates = FindFieldInHierarchy(type, "states");
                _bossStateIndex = FindFieldInHierarchy(type, "stateIndex");
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
            _bossStates = null;
            _bossStateIndex = null;
            _bossPropertiesLevel = null;
            _lastSentBossHp = float.MinValue;
            _lastBossHpSentAt = -10f;
        }

        public static void Reset()
        {
            _nextBroadcastAt = 0f;
            _nextRescanAt = 0f;
            _recoveryBurstFrames = 0;
            _hostReceivers.Clear();
            _animators.Clear();
            _lastSent.Clear();
            _lastReceivedTicks.Clear();
            _animMismatches.Clear();
            _identityWarnings.Clear();
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
        /// Moves local boss health to the host's value: downward through the
        /// game's damage path, upward phase-safely. Used by the desync sentinel.
        /// </summary>
        public static void ForceLocalBossHp(float hp)
        {
            float current;
            if (!TryGetLocalBossHp(out current) || current <= 0f)
                return;

            if (hp < current)
                DealBossDamage(current - hp);
            else if (hp > current)
                RaiseBossHpPhaseSafe(hp);
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
                    var cleaned = new List<string>(parts.Length);
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
