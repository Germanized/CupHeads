using System;
using System.Collections.Generic;
using HarmonyLib;
using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Creates visual-only runtime avatars for extra network participants beyond
    /// Cuphead's built-in PlayerOne / PlayerTwo slots.
    ///
    /// These avatars intentionally do NOT register with PlayerManager or run live
    /// gameplay logic. We clone a live player controller as a rendering template,
    /// disable the dangerous gameplay components, and then drive the clone's motor
    /// / animator from relayed network state.
    /// </summary>
    public static class ExtraRemoteAvatarManager
    {
        sealed class AvatarState
        {
            public byte ParticipantId;
            public LevelPlayerController Controller;
            public LevelPlayerMotor Motor;
            public LevelPlayerAnimationController Animation;
            public LevelPlayerWeaponManager WeaponManager;
            public float ShootingUntil;
            public Vector2 HitboxOffset;
            public Vector2 HitboxSize;
        }

        sealed class RenamedController
        {
            public AbstractPlayerController Controller;
            public string OriginalName;
        }

        static readonly Dictionary<byte, AvatarState> _avatars =
            new Dictionary<byte, AvatarState>(4);
        static readonly Dictionary<byte, PlayerStatePacket> _pendingStates =
            new Dictionary<byte, PlayerStatePacket>(4);
        static readonly Dictionary<int, byte> _motorParticipantIds =
            new Dictionary<int, byte>(4);
        static readonly Dictionary<int, byte> _controllerParticipantIds =
            new Dictionary<int, byte>(4);
        static readonly Dictionary<int, List<RenamedController>> _awakeRenames =
            new Dictionary<int, List<RenamedController>>(2);

        static bool _creatingAvatar;

        static ExtraRemoteAvatarManager()
        {
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static bool HasAvatar(byte participantId)
        {
            AvatarState avatar;
            return _avatars.TryGetValue(participantId, out avatar)
                && avatar != null
                && avatar.Controller != null;
        }

        public static int AppendTargetableControllers(List<AbstractPlayerController> target)
        {
            if (target == null)
                return 0;

            int added = 0;
            foreach (var entry in _avatars)
            {
                var avatar = entry.Value;
                if (avatar == null || avatar.Controller == null)
                    continue;
                if (!avatar.Controller.gameObject.activeInHierarchy)
                    continue;

                target.Add(avatar.Controller);
                added++;
            }

            return added;
        }

        public static int AppendParticipants(List<byte> target)
        {
            if (target == null)
                return 0;

            int added = 0;
            foreach (var entry in _avatars)
            {
                var avatar = entry.Value;
                if (avatar == null || avatar.Controller == null)
                    continue;
                if (!avatar.Controller.gameObject.activeInHierarchy)
                    continue;

                target.Add(entry.Key);
                added++;
            }

            return added;
        }

        public static bool TryGetHitbox(byte participantId, out Bounds bounds)
        {
            bounds = new Bounds();

            AvatarState avatar;
            if (!_avatars.TryGetValue(participantId, out avatar)
             || avatar == null
             || avatar.Controller == null)
            {
                return false;
            }

            Vector2 size2 = avatar.HitboxSize;
            if (size2.x <= 0.01f || size2.y <= 0.01f)
            {
                var collider = avatar.Controller.collider;
                if (collider != null)
                {
                    avatar.HitboxOffset = collider.offset;
                    avatar.HitboxSize = collider.size;
                    size2 = avatar.HitboxSize;
                }
            }

            if (size2.x > 0.01f && size2.y > 0.01f)
            {
                Vector3 center = avatar.Controller.transform.TransformPoint(avatar.HitboxOffset);
                Vector3 scale = avatar.Controller.transform.lossyScale;
                bounds = new Bounds(
                    center,
                    new Vector3(
                        Mathf.Abs(size2.x * scale.x),
                        Mathf.Abs(size2.y * scale.y),
                        1f));
                return true;
            }

            var renderers = avatar.Controller.GetComponentsInChildren<Renderer>(false);
            bool hasBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                    continue;

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds;
        }

        public static bool TryGetAvatarParticipantId(LevelPlayerMotor motor, out byte participantId)
        {
            participantId = byte.MaxValue;
            if (motor == null)
                return false;

            return _motorParticipantIds.TryGetValue(motor.GetInstanceID(), out participantId);
        }

        public static bool TryGetParticipantId(AbstractPlayerController controller, out byte participantId)
        {
            participantId = byte.MaxValue;
            if (controller == null)
                return false;

            return _controllerParticipantIds.TryGetValue(controller.GetInstanceID(), out participantId);
        }

        public static void NotifyState(PlayerStatePacket pkt)
        {
            if (pkt.PlayerId <= (byte)PlayerId.PlayerTwo)
                return;

            _pendingStates[pkt.PlayerId] = pkt;
            if (!TryEnsureAvatar(pkt.PlayerId))
                return;

            AvatarState avatar;
            if (_avatars.TryGetValue(pkt.PlayerId, out avatar))
                ApplyLifeState(avatar, pkt.IsDead);
        }

        public static void ApplyWeaponEvent(WeaponEventPacket pkt)
        {
            if (pkt.PlayerId <= (byte)PlayerId.PlayerTwo)
                return;

            if (!TryEnsureAvatar(pkt.PlayerId))
                return;

            AvatarState avatar;
            if (!_avatars.TryGetValue(pkt.PlayerId, out avatar) || avatar == null || avatar.Controller == null)
                return;

            if (avatar.Motor != null)
            {
                var dir = new Trilean2(pkt.AimX, pkt.AimY);
                var motorTraverse = Traverse.Create(avatar.Motor);
                motorTraverse.Property("LookDirection").SetValue(dir);
                motorTraverse.Property("TrueLookDirection").SetValue(dir);
            }

            switch (pkt.EventType)
            {
                case 0:
                    SetShootingState(avatar, true, 0.11f);
                    TriggerAnimatorParam(avatar.Controller, "Shooting", true);
                    break;

                case 1:
                    SetShootingState(avatar, true, 0.14f);
                    TriggerAnimatorTrigger(avatar.Controller, "Ex");
                    break;

                case 2:
                    SetShootingState(avatar, false, 0f);
                    TriggerAnimatorTrigger(avatar.Controller, "Super");
                    break;

                case 3:
                    TriggerAnimatorTrigger(avatar.Controller, "Parry");
                    break;

                case 4:
                    if (avatar.WeaponManager != null)
                        ApplyWeaponSwitch(avatar.WeaponManager, pkt.WeaponId);
                    break;
            }
        }

        public static void Update()
        {
            if (_pendingStates.Count > 0)
            {
                var pendingIds = new List<byte>(_pendingStates.Keys);
                for (int i = 0; i < pendingIds.Count; i++)
                    TryEnsureAvatar(pendingIds[i]);
            }

            if (_avatars.Count == 0)
                return;

            float now = Time.unscaledTime;
            var deadIds = new List<byte>();

            foreach (var entry in _avatars)
            {
                var avatar = entry.Value;
                if (avatar == null || avatar.Controller == null)
                {
                    deadIds.Add(entry.Key);
                    continue;
                }

                if (avatar.ShootingUntil > 0f && now >= avatar.ShootingUntil)
                    SetShootingState(avatar, false, 0f);
            }

            for (int i = 0; i < deadIds.Count; i++)
                RemoveParticipant(deadIds[i]);
        }

        public static void RemoveParticipant(byte participantId)
        {
            _pendingStates.Remove(participantId);
            RemoteInputDriver.Reset(participantId);
            RemotePlayer.Reset(participantId);

            AvatarState avatar;
            if (!_avatars.TryGetValue(participantId, out avatar))
                return;

            if (avatar != null && avatar.Motor != null)
                _motorParticipantIds.Remove(avatar.Motor.GetInstanceID());
            if (avatar != null && avatar.Controller != null)
                _controllerParticipantIds.Remove(avatar.Controller.GetInstanceID());

            if (avatar != null && avatar.Controller != null)
                UnityEngine.Object.Destroy(avatar.Controller.gameObject);

            _avatars.Remove(participantId);
        }

        public static void Reset()
        {
            var ids = new List<byte>(_avatars.Keys);
            for (int i = 0; i < ids.Count; i++)
                RemoveParticipant(ids[i]);

            _avatars.Clear();
            _pendingStates.Clear();
            _motorParticipantIds.Clear();
            _controllerParticipantIds.Clear();
            _awakeRenames.Clear();
            _creatingAvatar = false;
        }

        internal static void OnControllerAwakePrefix(AbstractPlayerController instance)
        {
            if (!_creatingAvatar || instance == null)
                return;

            int key = instance.GetInstanceID();
            if (_awakeRenames.ContainsKey(key))
                return;

            var renamed = new List<RenamedController>();
            var controllers = UnityEngine.Object.FindObjectsOfType<AbstractPlayerController>();
            for (int i = 0; i < controllers.Length; i++)
            {
                var controller = controllers[i];
                if (controller == null)
                    continue;

                string name = controller.name;
                if (string.IsNullOrEmpty(name)
                 || name.IndexOf("PlayerTwo", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                renamed.Add(new RenamedController
                {
                    Controller = controller,
                    OriginalName = name,
                });
                controller.name = name.Replace("PlayerTwo", "NetAvatarGuard");
            }

            _awakeRenames[key] = renamed;
        }

        internal static void OnControllerAwakePostfix(AbstractPlayerController instance)
        {
            if (instance == null)
                return;

            List<RenamedController> renamed;
            if (!_awakeRenames.TryGetValue(instance.GetInstanceID(), out renamed))
                return;

            for (int i = 0; i < renamed.Count; i++)
            {
                var entry = renamed[i];
                if (entry != null && entry.Controller != null)
                    entry.Controller.name = entry.OriginalName;
            }

            _awakeRenames.Remove(instance.GetInstanceID());
        }

        static bool TryEnsureAvatar(byte participantId)
        {
            AvatarState existing;
            if (_avatars.TryGetValue(participantId, out existing)
             && existing != null
             && existing.Controller != null)
            {
                return true;
            }

            var template = SelectTemplateController(participantId);
            if (template == null)
                return false;

            LevelPlayerController clone = null;
            _creatingAvatar = true;
            try
            {
                clone = UnityEngine.Object.Instantiate(template);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[ExtraAvatar] Clone creation failed: " + ex.Message);
            }
            finally
            {
                _creatingAvatar = false;
            }

            if (clone == null)
                return false;

            clone.transform.SetParent(template.transform.parent, true);
            clone.name = "NetworkParticipant_" + participantId;
            clone.gameObject.hideFlags = HideFlags.HideAndDontSave;

            ConfigureAvatar(clone);

            var avatar = new AvatarState
            {
                ParticipantId = participantId,
                Controller = clone,
                Motor = clone.motor,
                Animation = clone.animationController,
                WeaponManager = clone.weaponManager,
            };

            var box = clone.collider;
            if (box != null)
            {
                avatar.HitboxOffset = box.offset;
                avatar.HitboxSize = box.size;
            }

            _avatars[participantId] = avatar;
            if (avatar.Motor != null)
                _motorParticipantIds[avatar.Motor.GetInstanceID()] = participantId;
            _controllerParticipantIds[clone.GetInstanceID()] = participantId;

            PlayerStatePacket initialState;
            if (_pendingStates.TryGetValue(participantId, out initialState))
            {
                clone.transform.position = new Vector3(
                    initialState.PosX,
                    initialState.PosY,
                    clone.transform.position.z);
                ApplyLifeState(avatar, initialState.IsDead);
            }

            Plugin.Log.LogInfo("[ExtraAvatar] Spawned visual avatar for participant #" + participantId + ".");
            return true;
        }

        static LevelPlayerController SelectTemplateController(byte participantId)
        {
            LevelPlayerController preferred = null;
            if ((participantId & 1) == 0)
                preferred = MultiplayerSession.GetRemoteController() ?? MultiplayerSession.GetLocalController();
            else
                preferred = MultiplayerSession.GetLocalController() ?? MultiplayerSession.GetRemoteController();

            if (preferred != null && preferred.gameObject != null)
                return preferred;

            var controllers = UnityEngine.Object.FindObjectsOfType<LevelPlayerController>();
            for (int i = 0; i < controllers.Length; i++)
            {
                if (controllers[i] != null)
                    return controllers[i];
            }

            return null;
        }

        static void ConfigureAvatar(LevelPlayerController controller)
        {
            if (controller == null)
                return;

            if (controller.input != null)
                controller.input.enabled = false;

            if (controller.cameraController != null)
                controller.cameraController.enabled = false;

            if (controller.damageReceiver != null)
                controller.damageReceiver.enabled = false;

            if (controller.parryController != null)
                controller.parryController.enabled = false;

            if (controller.colliderManager != null)
                controller.colliderManager.enabled = false;

            if (controller.weaponManager != null)
            {
                controller.weaponManager.DisableInput();
                controller.weaponManager.enabled = false;
            }

            if (controller.stats != null)
                controller.stats.enabled = false;

            var components = controller.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null)
                    continue;

                var behaviour = component as Behaviour;
                string typeName = component.GetType().Name;
                if (behaviour != null
                 && (typeName.EndsWith("Collider2D", StringComparison.Ordinal)
                  || typeName == "PlayerDamageReceiver"))
                {
                    behaviour.enabled = false;
                    continue;
                }

                if (typeName != "Rigidbody2D")
                    continue;

                var type = component.GetType();
                var velocity = type.GetProperty("velocity");
                var angularVelocity = type.GetProperty("angularVelocity");
                var simulated = type.GetProperty("simulated");
                velocity?.SetValue(component, Vector2.zero, null);
                angularVelocity?.SetValue(component, 0f, null);
                simulated?.SetValue(component, false, null);
            }
        }

        static void SetShootingState(AvatarState avatar, bool shooting, float duration)
        {
            if (avatar == null)
                return;

            if (avatar.WeaponManager != null)
                avatar.WeaponManager.IsShooting = shooting;

            avatar.ShootingUntil = shooting ? Time.unscaledTime + duration : 0f;

            if (!shooting && avatar.Controller != null)
                TriggerAnimatorParam(avatar.Controller, "Shooting", false);
        }

        static void ApplyLifeState(AvatarState avatar, bool isDead)
        {
            if (avatar == null || avatar.Controller == null || avatar.Controller.gameObject == null)
                return;

            bool shouldBeActive = !isDead;
            if (avatar.Controller.gameObject.activeSelf == shouldBeActive)
                return;

            avatar.Controller.gameObject.SetActive(shouldBeActive);
        }

        static void TriggerAnimatorParam(LevelPlayerController player, string param, bool value)
        {
            var anim = player != null ? player.animationController?.animator : null;
            if (anim != null)
                anim.SetBool(param, value);
        }

        static void TriggerAnimatorTrigger(LevelPlayerController player, string trigger)
        {
            var anim = player != null ? player.animationController?.animator : null;
            if (anim != null)
                anim.SetTrigger(trigger);
        }

        static void ApplyWeaponSwitch(LevelPlayerWeaponManager wm, byte weaponId)
        {
            try
            {
                var weapon = LoadoutCodec.DecodeWeapon(weaponId, primarySlot: true);
                var mi = typeof(LevelPlayerWeaponManager).GetMethod(
                    "SwitchWeapon",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                mi?.Invoke(wm, new object[] { weapon });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[ExtraAvatar] Weapon switch failed: " + ex.Message);
            }
        }
    }

    [HarmonyPatch(typeof(AbstractPlayerController), "Awake")]
    public static class ExtraRemoteAvatarAwakePatch
    {
        static void Prefix(AbstractPlayerController __instance)
        {
            ExtraRemoteAvatarManager.OnControllerAwakePrefix(__instance);
        }

        static void Postfix(AbstractPlayerController __instance)
        {
            ExtraRemoteAvatarManager.OnControllerAwakePostfix(__instance);
        }
    }
}
