using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using CupheadOnline.Sync;
using UnityEngine;

namespace CupheadOnline.Patches
{
    static class ExtraInteractionBridge
    {
        static readonly BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        static readonly PropertyInfo StateProperty =
            typeof(AbstractLevelInteractiveEntity).GetProperty("state", AnyInstance);
        static readonly PropertyInfo PlayerActivatingProperty =
            typeof(AbstractLevelInteractiveEntity).GetProperty("playerActivating", AnyInstance);
        static readonly FieldInfo DialogueField =
            typeof(AbstractLevelInteractiveEntity).GetField("dialogue", AnyInstance);
        static readonly FieldInfo OnActivateEventField =
            AccessTools.Field(typeof(AbstractLevelInteractiveEntity), "OnActivateEvent");
        static readonly FieldInfo HouseExitActivatedField =
            typeof(HouseLevelExit).GetField("activated", AnyInstance);
        static readonly FieldInfo HouseExitSceneField =
            typeof(HouseLevelExit).GetField("sceneLoadOnExit", AnyInstance);

        static readonly List<AbstractPlayerController> ScratchExtras =
            new List<AbstractPlayerController>(6);
        static readonly List<AbstractPlayerController> ScratchAliveParticipants =
            new List<AbstractPlayerController>(8);

        internal static void HandleExtraInteraction(AbstractLevelInteractiveEntity instance)
        {
            if (!ShouldBridge(instance))
                return;
            if (StateProperty == null || PlayerActivatingProperty == null || DialogueField == null)
                return;
            if (GetStateValue(instance) == 2)
                return;

            var interactor = instance.interactor;
            if (interactor != AbstractLevelInteractiveEntity.Interactor.Either
             && interactor != AbstractLevelInteractiveEntity.Interactor.Both)
            {
                return;
            }

            ScratchExtras.Clear();
            ExtraRemoteAvatarManager.AppendTargetableControllers(ScratchExtras);
            if (ScratchExtras.Count <= 0)
                return;

            for (int i = 0; i < ScratchExtras.Count; i++)
            {
                var controller = ScratchExtras[i];
                if (!IsEligibleInteractor(instance, controller))
                    continue;

                byte participantId;
                if (!ExtraRemoteAvatarManager.TryGetParticipantId(controller, out participantId))
                    continue;
                if (!RemoteInputDriver.WasPressedThisFrame(participantId, (CupheadButton)13))
                    continue;

                if (interactor == AbstractLevelInteractiveEntity.Interactor.Both
                 && !HasPartnerInteractor(instance, participantId))
                {
                    continue;
                }

                Activate(instance, controller);
                return;
            }
        }

        internal static bool HandleHouseExit(HouseLevelExit instance)
        {
            if (!ShouldBridge(instance) || instance == null)
                return false;
            if (PlayerActivatingProperty == null || HouseExitActivatedField == null || HouseExitSceneField == null)
                return false;

            var activator = PlayerActivatingProperty.GetValue(instance, null) as AbstractPlayerController;
            byte participantId;
            if (!ExtraRemoteAvatarManager.TryGetParticipantId(activator, out participantId))
                return false;

            if (Convert.ToBoolean(HouseExitActivatedField.GetValue(instance)))
                return false;

            HouseExitActivatedField.SetValue(instance, true);
            var levelPlayer = activator as LevelPlayerController;
            if (levelPlayer != null)
            {
                levelPlayer.DisableInput();
                levelPlayer.PauseAll();
                levelPlayer.gameObject.SetActive(false);
            }

            var scene = (Scenes)HouseExitSceneField.GetValue(instance);
            instance.StartCoroutine(LoadHouseExitScene(instance, scene));
            return true;
        }

        static IEnumerator LoadHouseExitScene(HouseLevelExit instance, Scenes scene)
        {
            yield return CupheadTime.WaitForSeconds(instance, 1f);
            SceneLoader.LoadScene(
                scene,
                SceneLoader.Transition.Iris,
                SceneLoader.Transition.Iris,
                SceneLoader.Icon.Hourglass,
                null);
        }

        static bool ShouldBridge(AbstractLevelInteractiveEntity instance)
        {
            return MultiplayerSession.IsActive
                && MultiplayerSession.IsHost
                && instance != null
                && ExtraParticipantTracker.LiveCount > 0;
        }

        static int GetStateValue(AbstractLevelInteractiveEntity instance)
        {
            if (StateProperty == null || instance == null)
                return 0;

            object raw = StateProperty.GetValue(instance, null);
            return raw == null ? 0 : Convert.ToInt32(raw);
        }

        static bool IsEligibleInteractor(AbstractLevelInteractiveEntity instance, AbstractPlayerController controller)
        {
            if (instance == null || controller == null || controller.IsDead || !controller.gameObject.activeInHierarchy)
                return false;
            if (!IsWithinDistance(instance, controller))
                return false;

            var motor = controller as LevelPlayerController;
            return motor == null || !motor.motor.Dashing;
        }

        static bool IsWithinDistance(AbstractLevelInteractiveEntity instance, AbstractPlayerController controller)
        {
            Vector2 interactionPoint = (Vector2)instance.transform.position + instance.interactionPoint;
            Vector2 playerPos = controller.transform.position;
            return Vector2.Distance(interactionPoint, playerPos) <= instance.interactionDistance;
        }

        static bool HasPartnerInteractor(AbstractLevelInteractiveEntity instance, byte requestingParticipantId)
        {
            ScratchAliveParticipants.Clear();
            AppendAliveParticipants(ScratchAliveParticipants);

            for (int i = 0; i < ScratchAliveParticipants.Count; i++)
            {
                var participant = ScratchAliveParticipants[i];
                if (participant == null || !IsWithinDistance(instance, participant))
                    continue;

                byte participantId = ResolveParticipantId(participant);
                if (participantId == requestingParticipantId)
                    continue;

                if (participantId <= (byte)PlayerId.PlayerTwo)
                {
                    var player = PlayerManager.GetPlayer((PlayerId)participantId);
                    if (IsBuiltInInteractHeld(player))
                    {
                        return true;
                    }
                    continue;
                }

                if (RemoteInputDriver.IsPressed(participantId, (CupheadButton)13))
                    return true;
            }

            return false;
        }

        static void AppendAliveParticipants(List<AbstractPlayerController> target)
        {
            var playerOne = PlayerManager.GetPlayer(PlayerId.PlayerOne);
            if (playerOne != null && !playerOne.IsDead && playerOne.gameObject.activeInHierarchy)
                target.Add(playerOne);

            var playerTwo = PlayerManager.GetPlayer(PlayerId.PlayerTwo);
            if (playerTwo != null && !playerTwo.IsDead && playerTwo.gameObject.activeInHierarchy)
                target.Add(playerTwo);

            ExtraRemoteAvatarManager.AppendTargetableControllers(target);
        }

        static byte ResolveParticipantId(AbstractPlayerController controller)
        {
            byte participantId;
            if (ExtraRemoteAvatarManager.TryGetParticipantId(controller, out participantId))
                return participantId;

            return (byte)controller.id;
        }

        static void Activate(AbstractLevelInteractiveEntity instance, AbstractPlayerController controller)
        {
            if (instance == null || controller == null)
                return;

            var dialogue = DialogueField.GetValue(instance) as LevelUIInteractionDialogue;
            if (dialogue != null)
            {
                dialogue.Close();
                DialogueField.SetValue(instance, null);
            }

            PlayerActivatingProperty.SetValue(instance, controller, null);
            StateProperty.SetValue(instance, Enum.ToObject(StateProperty.PropertyType, 2), null);

            var onActivate = OnActivateEventField == null ? null : OnActivateEventField.GetValue(instance) as Action;
            if (onActivate != null)
                onActivate();

            var activateMethod = AccessTools.Method(instance.GetType(), "Activate", Type.EmptyTypes);
            if (activateMethod != null)
                activateMethod.Invoke(instance, null);
        }

        static bool IsBuiltInInteractHeld(AbstractPlayerController player)
        {
            if (player == null || player.input == null || player.input.actions == null)
                return false;

            // Goes through the Rewired routing patches, so a network-controlled
            // slot reports its remote owner's button.
            return player.input.actions.GetButton((int)CupheadButton.Accept);
        }
    }

    [HarmonyPatch(typeof(AbstractLevelInteractiveEntity), "FixedUpdate")]
    public static class AbstractLevelInteractiveEntityExtraPatch
    {
        static void Postfix(AbstractLevelInteractiveEntity __instance)
        {
            ExtraInteractionBridge.HandleExtraInteraction(__instance);
        }
    }

    [HarmonyPatch(typeof(HouseLevelExit), "Activate")]
    public static class HouseLevelExitExtraPatch
    {
        static bool Prefix(HouseLevelExit __instance)
        {
            return !ExtraInteractionBridge.HandleHouseExit(__instance);
        }
    }
}
