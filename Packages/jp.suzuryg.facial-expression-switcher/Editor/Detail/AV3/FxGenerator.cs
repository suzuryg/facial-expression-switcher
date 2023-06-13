﻿using AnimatorAsCode.V0;
using AnimatorAsCode.V0.Extensions.VRChat;
using nadena.dev.modular_avatar.core;
using Suzuryg.FacialExpressionSwitcher.Domain;
using Suzuryg.FacialExpressionSwitcher.UseCase;
using Suzuryg.FacialExpressionSwitcher.Detail.Drawing;
using Suzuryg.FacialExpressionSwitcher.External.Hai.ComboGestureIntegrator;
using System;
using System.Linq;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine.UIElements;
using UnityEditor.IMGUI.Controls;
using UniRx;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using VRC.SDK3.Avatars.ScriptableObjects;
using Suzuryg.FacialExpressionSwitcher.Detail.Localization;
using ExParam = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;
using ExType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;
using Sync = nadena.dev.modular_avatar.core.ParameterSyncType;

namespace Suzuryg.FacialExpressionSwitcher.Detail.AV3
{
    public class FxGenerator : IFxGenerator
    {
        private static readonly bool WriteDefaultsValue = false;

        private IReadOnlyLocalizationSetting _localizationSetting;
        private ModeNameProvider _modeNameProvider;
        private ExMenuThumbnailDrawer _exMenuThumbnailDrawer;
        private AV3Setting _aV3Setting;

        public FxGenerator(IReadOnlyLocalizationSetting localizationSetting, ModeNameProvider modeNameProvider, ExMenuThumbnailDrawer exMenuThumbnailDrawer, AV3Setting aV3Setting)
        {
            _localizationSetting = localizationSetting;
            _modeNameProvider = modeNameProvider;
            _exMenuThumbnailDrawer = exMenuThumbnailDrawer;
            _aV3Setting = aV3Setting;
        }

        public void Generate(IMenu menu) => Generate(menu, false);

        public void Generate(IMenu menu, bool forceOverLimitMode = false)
        {
            try
            {
                //UnityEngine.Profiling.Profiler.BeginSample("FxGenerator");
                EditorUtility.DisplayProgressBar(DomainConstants.SystemName, "Start FX controller generation.", 0);

                EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Creating folder...", 0);
                var generatedDir = AV3Constants.Path_GeneratedDir + DateTime.Now.ToString("/yyyyMMdd_HHmmss");
                AV3Utility.CreateFolderRecursively(generatedDir);

                var fxPath = generatedDir + "/FES_FX.controller";
                var exMenuPath = generatedDir + "/FES_ExMenu.asset";
                var cgeContainerPath = generatedDir + "/CgeIntegratorContainer.controller";

                // Copy template FX controller
                EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Creating fx controller...", 0);
                if (AssetDatabase.LoadAssetAtPath<AnimatorController>(AV3Constants.Path_FxTemplate) == null)
                {
                    throw new FacialExpressionSwitcherException("FX template was not found.");
                }
                else if (!AssetDatabase.CopyAsset(AV3Constants.Path_FxTemplate, fxPath))
                {
                    Debug.LogError(fxPath);
                    throw new FacialExpressionSwitcherException("Failed to copy FX template.");
                }
                var animatorController = AssetDatabase.LoadAssetAtPath<AnimatorController>(fxPath);

                // Initialize CGE Integrator container
                EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Creating container...", 0);
                var integratorContainer = new AnimatorController();
                AssetDatabase.CreateAsset(integratorContainer, cgeContainerPath);

                // Generate layers
                EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Generating layers...", 0);
                var avatarDescriptor = _aV3Setting.TargetAvatar;
                if (avatarDescriptor == null)
                {
                    throw new FacialExpressionSwitcherException("AvatarDescriptor was not found.");
                }
                var aac = AacV0.Create(GetConfiguration(avatarDescriptor, animatorController, WriteDefaultsValue));
                var modes = AV3Utility.FlattenMenuItemList(menu.Registered, _modeNameProvider);
                var defaultModeIndex = GetDefaultModeIndex(modes, menu);
                var emoteCount = GetEmoteCount(modes);
                var useOverLimitMode = forceOverLimitMode || emoteCount > AV3Constants.MaxEmoteNum;
                GenerateFaceEmoteSetControlLayer(modes, aac, animatorController, useOverLimitMode);
                GenerateDefaultFaceLayer(aac, avatarDescriptor, animatorController);
                GenerateFaceEmotePlayerLayer(modes, _aV3Setting, aac, animatorController, useOverLimitMode);
                ModifyBlinkLayer(aac, avatarDescriptor, animatorController);
                ModifyMouthMorphCancelerLayer(_aV3Setting, aac, avatarDescriptor, animatorController);
                if (_aV3Setting.SmoothAnalogFist)
                {
                    ComboGestureIntegratorProxy.DoGenerate(animatorController, integratorContainer, WriteDefaultsValue);
                }

                // Generate MA Object
                EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Generating ExMenu...", 0);
                var exMenu = GenerateExMenu(modes, menu, exMenuPath, useOverLimitMode);

                EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Generating MA root object...", 0);
                var rootObject = GetMARootObject(avatarDescriptor);
                if (rootObject == null)
                {
                    throw new FacialExpressionSwitcherException("Failed to get MA root object.");
                }

                EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Generating MA components...", 0);
                AddMergeAnimatorComponent(rootObject, animatorController);
                AddMenuInstallerComponent(rootObject, exMenu);
                AddParameterComponent(rootObject, defaultModeIndex);

                AddBlinkDisablerComponent(rootObject);
                AddTrackingControlDisablerComponent(rootObject);

                // Instantiate prefabs
                EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Instantiating prefabs...", 0);
                InstantiatePrefabs(rootObject);

                // Clean assets
                EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Cleaning assets...", 0);
                CleanAssets();

                EditorUtility.DisplayProgressBar(DomainConstants.SystemName, "Done!", 1);
            }
            finally
            {
                //UnityEngine.Profiling.Profiler.EndSample();
                EditorUtility.ClearProgressBar();
            }
        }

        private static void GenerateFaceEmoteSetControlLayer(IReadOnlyList<ModeEx> modes, AacFlBase aac, AnimatorController animatorController, bool useOverLimitMode)
        {
            // Replace existing layer
            var layerName = AV3Constants.LayerName_FaceEmoteSetControl;
            if (!animatorController.layers.Any(x => x.name == layerName))
            {
                throw new FacialExpressionSwitcherException($"The layer \"{layerName}\" was not found in FX template.");
            }
            var layer = aac.CreateSupportingArbitraryControllerLayer(animatorController, layerName);
            AV3Utility.SetLayerWeight(animatorController, layerName, 0);
            layer.StateMachine.WithEntryPosition(0, -1).WithAnyStatePosition(0, -2).WithExitPosition(3, 0);

            // Create initializing states
            var init = layer.NewState("INIT", 0, 0);
            var gate = layer.NewState("GATE", 1, 0);
            init.TransitionsTo(gate).
                When(layer.Av3().IsLocal.IsEqualTo(true));

            // Create each mode's sub-state machine
            for (int modeIndex = 0; modeIndex < modes.Count; modeIndex++)
            {
                EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Generating \"{layerName}\" layer...",
                    1f / modes.Count * modeIndex);

                var mode = modes[modeIndex];
                var modeStateMachine = layer.NewSubStateMachine(mode.PathToMode, 2, modeIndex)
                    .WithEntryPosition(0, 0).WithAnyStatePosition(0, -1).WithParentStateMachinePosition(0, -2).WithExitPosition(2, 0);
                gate.TransitionsTo(modeStateMachine)
                    .When(layer.IntParameter(AV3Constants.ParamName_EM_EMOTE_PATTERN).IsEqualTo(modeIndex));
                modeStateMachine.TransitionsTo(modeStateMachine)
                    .When(layer.IntParameter(AV3Constants.ParamName_EM_EMOTE_PATTERN).IsEqualTo(modeIndex));
                modeStateMachine.Exits()
                    .When(layer.IntParameter(AV3Constants.ParamName_EM_EMOTE_PATTERN).IsNotEqualTo(modeIndex));

                // Check if the mode has branches
                var hasBranches = modes[modeIndex].Mode.Branches.Count > 0;

                if (hasBranches)
                {
                    // Create each gesture's state
                    for (int leftIndex = 0; leftIndex < AV3Constants.EmoteSelectToGesture.Count; leftIndex++)
                    {
                        for (int rightIndex = 0; rightIndex < AV3Constants.EmoteSelectToGesture.Count; rightIndex++)
                        {
                            EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Generating \"{layerName}\" layer...",
                                1f / modes.Count * modeIndex 
                                + 1f / modes.Count / AV3Constants.EmoteSelectToGesture.Count * leftIndex 
                                + 1f / modes.Count / AV3Constants.EmoteSelectToGesture.Count / AV3Constants.EmoteSelectToGesture.Count * rightIndex);

                            var preSelectEmoteIndex = AV3Utility.GetPreselectEmoteIndex(AV3Constants.EmoteSelectToGesture[leftIndex], AV3Constants.EmoteSelectToGesture[rightIndex]);
                            var gestureState = modeStateMachine.NewState($"L{leftIndex} R{rightIndex}", 1, preSelectEmoteIndex);
                            gestureState.TransitionsFromEntry()
                                .When(layer.IntParameter(AV3Constants.ParamName_EM_EMOTE_PRESELECT).IsEqualTo(preSelectEmoteIndex));
                            gestureState.Exits()
                                .When(layer.IntParameter(AV3Constants.ParamName_EM_EMOTE_PRESELECT).IsNotEqualTo(preSelectEmoteIndex))
                                .Or()
                                .When(layer.IntParameter(AV3Constants.ParamName_EM_EMOTE_PATTERN).IsNotEqualTo(modeIndex));

                            // Add parameter driver
                            var branchIndex = GetBranchIndex(AV3Constants.EmoteSelectToGesture[leftIndex], AV3Constants.EmoteSelectToGesture[rightIndex], mode.Mode);
                            var emoteIndex = GetEmoteIndex(branchIndex, mode, useOverLimitMode);
                            gestureState.Drives(layer.IntParameter(AV3Constants.ParamName_SYNC_EM_EMOTE), emoteIndex).DrivingLocally();
                        }
                    }
                }
                else
                {
                    // Create single state
                    var state = modeStateMachine.NewState($"Any Gestures", 1, 0);
                    state.TransitionsFromEntry();
                    state.Exits()
                        .When(layer.IntParameter(AV3Constants.ParamName_EM_EMOTE_PATTERN).IsNotEqualTo(modeIndex));

                    // Add parameter driver
                    var emoteIndex = GetEmoteIndex(branchIndex: -1, mode, useOverLimitMode);
                    state.Drives(layer.IntParameter(AV3Constants.ParamName_SYNC_EM_EMOTE), emoteIndex).DrivingLocally();
                }
            }

            EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Generating \"{layerName}\" layer...", 1);
        }

        private void GenerateDefaultFaceLayer(AacFlBase aac, VRCAvatarDescriptor avatarDescriptor, AnimatorController animatorController)
        {
            var layerName = AV3Constants.LayerName_DefaultFace;

            EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Generating \"{layerName}\" layer...", 0);

            // Replace existing layer
            if (!animatorController.layers.Any(x => x.name == layerName))
            {
                throw new FacialExpressionSwitcherException($"The layer \"{layerName}\" was not found in FX template.");
            }
            var layer = aac.CreateSupportingArbitraryControllerLayer(animatorController, layerName);
            layer.StateMachine.WithEntryPosition(0, -1).WithAnyStatePosition(0, -2).WithExitPosition(0, -3);

            // Create default face state
            var defaultFace = GetDefaultFaceAnimation(aac, avatarDescriptor);
            layer.NewState("DEFAULT", 0, 0).WithAnimation(defaultFace);

            EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Generating \"{layerName}\" layer...", 1);
        }

        private static void GenerateFaceEmotePlayerLayer(IReadOnlyList<ModeEx> modes, AV3Setting aV3Setting, AacFlBase aac, AnimatorController animatorController, bool useOverLimitMode)
        {
            var layerName = AV3Constants.LayerName_FaceEmotePlayer;

            EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Generating \"{layerName}\" layer...", 0);

            // Replace existing layer
            if (!animatorController.layers.Any(x => x.name == layerName))
            {
                throw new FacialExpressionSwitcherException($"The layer \"{layerName}\" was not found in FX template.");
            }
            var layer = aac.CreateSupportingArbitraryControllerLayer(animatorController, layerName);
            layer.StateMachine.WithEntryPosition(0, 0).WithAnyStatePosition(2, -2).WithExitPosition(2, 0);

            // Create not-afk sub-state machine
            var notAfkStateMachine = layer.NewSubStateMachine("Not AFK", 1, 0)
                .WithEntryPosition(0, 0).WithAnyStatePosition(0, -1).WithParentStateMachinePosition(0, -2).WithExitPosition(2, 0);
            notAfkStateMachine.TransitionsFromEntry()
                .When(layer.Av3().AFK.IsFalse());
            notAfkStateMachine.Exits();

            // Create face emote playing states
            var emptyClip = aac.NewClip();
            var emptyName = "Empty";
            for (int modeIndex = 0; modeIndex < modes.Count; modeIndex++)
            {
                EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Generating \"{layerName}\" layer...",
                    1f / modes.Count * modeIndex);

                var mode = modes[modeIndex];
                var stateMachine = notAfkStateMachine;

                // If use over-limit mode, create sub-state machines
                if (useOverLimitMode)
                {
                    var modeStateMachine = stateMachine.NewSubStateMachine(mode.PathToMode, 1, modeIndex)
                        .WithEntryPosition(0, 0).WithAnyStatePosition(0, -1).WithParentStateMachinePosition(0, -2).WithExitPosition(2, 0);
                    modeStateMachine.TransitionsFromEntry()
                        .When(layer.IntParameter(AV3Constants.ParamName_EM_EMOTE_PATTERN).IsEqualTo(modeIndex));
                    modeStateMachine.Exits();
                    stateMachine = modeStateMachine;
                }

                for (int branchIndex = -1; branchIndex < mode.Mode.Branches.Count; branchIndex++)
                {
                    EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Generating \"{layerName}\" layer...",
                        1f / modes.Count * modeIndex
                        + 1f / modes.Count / (mode.Mode.Branches.Count + 1) * (branchIndex + 1));

                    var emoteIndex = GetEmoteIndex(branchIndex, mode, useOverLimitMode);
                    AacFlState emoteState;
                    // Mode
                    if (branchIndex < 0)
                    {
                        var animation = AV3Utility.GetAnimationClipWithName(mode.Mode.Animation);
                        emoteState = stateMachine.NewState(animation.name ?? emptyName, 1, emoteIndex)
                            .WithAnimation(animation.clip ?? emptyClip.Clip);

                        emoteState
                            .Drives(layer.BoolParameter(AV3Constants.ParamName_CN_BLINK_ENABLE), mode.Mode.BlinkEnabled)
                            .Drives(layer.BoolParameter(AV3Constants.ParamName_CN_MOUTH_MORPH_CANCEL_ENABLE), mode.Mode.MouthMorphCancelerEnabled && mode.Mode.MouthTrackingControl == MouthTrackingControl.Tracking)
                            .TrackingSets(TrackingElement.Eyes, AV3Utility.ConvertEyeTrackingType(mode.Mode.EyeTrackingControl))
                            .TrackingSets(TrackingElement.Mouth, AV3Utility.ConvertMouthTrackingType(mode.Mode.MouthTrackingControl));
                    }
                    // Branches
                    else
                    {
                        (Motion motion, string name) motion = (null, null);
                        var branch = mode.Mode.Branches[branchIndex];
                        var baseAnimation = AV3Utility.GetAnimationClipWithName(branch.BaseAnimation);
                        var leftAnimation = AV3Utility.GetAnimationClipWithName(branch.LeftHandAnimation);
                        var rightAnimation = AV3Utility.GetAnimationClipWithName(branch.RightHandAnimation);
                        var bothAnimation = AV3Utility.GetAnimationClipWithName(branch.BothHandsAnimation);
                        var leftWeight = aV3Setting.SmoothAnalogFist ? AV3Constants.ParamName_GestureLWSmoothing : AV3Constants.ParamName_GestureLeftWeight;
                        var rightWeight = aV3Setting.SmoothAnalogFist ? AV3Constants.ParamName_GestureRWSmoothing : AV3Constants.ParamName_GestureRightWeight;

                        // Both triggers used
                        if (branch.CanLeftTriggerUsed && branch.IsLeftTriggerUsed && branch.CanRightTriggerUsed && branch.IsRightTriggerUsed)
                        {
                            var blendTree = aac.NewBlendTreeAsRaw();
                            blendTree.blendType = BlendTreeType.FreeformCartesian2D;
                            blendTree.useAutomaticThresholds = false;
                            blendTree.blendParameter = leftWeight;
                            blendTree.blendParameterY = rightWeight;
                            blendTree.AddChild(baseAnimation.clip ?? emptyClip.Clip, new Vector2(0, 0));
                            blendTree.AddChild(leftAnimation.clip ?? emptyClip.Clip, new Vector2(1, 0));
                            blendTree.AddChild(rightAnimation.clip ?? emptyClip.Clip, new Vector2(0, 1));
                            blendTree.AddChild(bothAnimation.clip ?? emptyClip.Clip, new Vector2(1, 1));
                            motion = (blendTree, $"{baseAnimation.name ?? emptyName}_{leftAnimation.name ?? emptyName}_{rightAnimation.name ?? emptyName}_{bothAnimation.name ?? emptyName}");
                        }
                        // Left trigger used
                        else if (branch.CanLeftTriggerUsed && branch.IsLeftTriggerUsed)
                        {
                            var blendTree = aac.NewBlendTreeAsRaw();
                            blendTree.blendType = BlendTreeType.Simple1D;
                            blendTree.useAutomaticThresholds = false;
                            blendTree.blendParameter = leftWeight;
                            blendTree.AddChild(baseAnimation.clip ?? emptyClip.Clip, 0);
                            blendTree.AddChild(leftAnimation.clip ?? emptyClip.Clip, 1);
                            motion = (blendTree, $"{baseAnimation.name ?? emptyName}_{leftAnimation.name ?? emptyName}");
                        }
                        // Right trigger used
                        else if (branch.CanRightTriggerUsed && branch.IsRightTriggerUsed)
                        {
                            var blendTree = aac.NewBlendTreeAsRaw();
                            blendTree.blendType = BlendTreeType.Simple1D;
                            blendTree.useAutomaticThresholds = false;
                            blendTree.blendParameter = rightWeight;
                            blendTree.AddChild(baseAnimation.clip ?? emptyClip.Clip, 0);
                            blendTree.AddChild(rightAnimation.clip ?? emptyClip.Clip, 1);
                            motion = (blendTree, $"{baseAnimation.name ?? emptyName}_{rightAnimation.name ?? emptyName}");
                        }
                        // No triggers used
                        else
                        {
                            motion = (baseAnimation.clip ?? emptyClip.Clip, baseAnimation.name ?? emptyName);
                        }

                        emoteState = stateMachine.NewState(motion.name, 1, emoteIndex)
                            .WithAnimation(motion.motion)
                            .Drives(layer.BoolParameter(AV3Constants.ParamName_CN_BLINK_ENABLE), branch.BlinkEnabled)
                            .Drives(layer.BoolParameter(AV3Constants.ParamName_CN_MOUTH_MORPH_CANCEL_ENABLE), branch.MouthMorphCancelerEnabled && branch.MouthTrackingControl == MouthTrackingControl.Tracking)
                            .TrackingSets(TrackingElement.Eyes, AV3Utility.ConvertEyeTrackingType(branch.EyeTrackingControl))
                            .TrackingSets(TrackingElement.Mouth, AV3Utility.ConvertMouthTrackingType(branch.MouthTrackingControl));
                    }

                    emoteState.TransitionsFromEntry()
                        .When(layer.IntParameter(AV3Constants.ParamName_SYNC_EM_EMOTE).IsEqualTo(emoteIndex));

                    var exitEmote = emoteState.Exits()
                        .WithTransitionDurationSeconds((float)aV3Setting.TransitionDurationSeconds)
                        .When(layer.Av3().AFK.IsTrue())
                        .Or()
                        .When(layer.IntParameter(AV3Constants.ParamName_SYNC_EM_EMOTE).IsNotEqualTo(emoteIndex))
                        .And(layer.BoolParameter(AV3Constants.ParamName_SYNC_CN_WAIT_FACE_EMOTE_BY_VOICE).IsFalse())
                        .Or()
                        .When(layer.IntParameter(AV3Constants.ParamName_SYNC_EM_EMOTE).IsNotEqualTo(emoteIndex))
                        .And(layer.BoolParameter(AV3Constants.ParamName_SYNC_CN_WAIT_FACE_EMOTE_BY_VOICE).IsTrue())
                        .And(layer.Av3().Voice.IsLessThan(AV3Constants.VoiceThreshold));

                    // If use over-limit mode, extra-exit-transition is needed
                    if (useOverLimitMode)
                    {
                        var exitEmoteOverlimit = emoteState.Exits()
                            .WithTransitionDurationSeconds((float)aV3Setting.TransitionDurationSeconds)
                            .When(layer.IntParameter(AV3Constants.ParamName_EM_EMOTE_PATTERN).IsNotEqualTo(modeIndex))
                            .And(layer.BoolParameter(AV3Constants.ParamName_SYNC_CN_WAIT_FACE_EMOTE_BY_VOICE).IsFalse())
                            .Or()
                            .When(layer.IntParameter(AV3Constants.ParamName_EM_EMOTE_PATTERN).IsNotEqualTo(modeIndex))
                            .And(layer.BoolParameter(AV3Constants.ParamName_SYNC_CN_WAIT_FACE_EMOTE_BY_VOICE).IsTrue())
                            .And(layer.Av3().Voice.IsLessThan(AV3Constants.VoiceThreshold));
                    }
                }

                // If use over-limit mode, extra-exit-state is needed
                if (useOverLimitMode)
                {
                    var exitState = stateMachine.NewState("MODE CHANGE", 1, -1);
                    exitState.TransitionsFromEntry()
                        .When(layer.IntParameter(AV3Constants.ParamName_EM_EMOTE_PATTERN).IsNotEqualTo(modeIndex));
                    exitState.Exits()
                        .When(layer.BoolParameter(AV3Constants.ParamName_Dummy).IsFalse());
                }
            }

            // Create AFK sub-state machine
            var afkStateMachine = layer.NewSubStateMachine("AFK", 1, -1)
                .WithEntryPosition(0, 0).WithAnyStatePosition(0, -1).WithParentStateMachinePosition(0, -2).WithExitPosition(0, 5);
            layer.EntryTransitionsTo(afkStateMachine)
                .When(layer.Av3().AFK.IsTrue());
            afkStateMachine.Exits();

            var afkStandbyState = afkStateMachine.NewState("AFK Standby", 0, 1)
                .Drives(layer.BoolParameter(AV3Constants.ParamName_CN_BLINK_ENABLE), true)
                .Drives(layer.BoolParameter(AV3Constants.ParamName_CN_MOUTH_MORPH_CANCEL_ENABLE), true)
                .TrackingSets(TrackingElement.Eyes, VRC_AnimatorTrackingControl.TrackingType.Tracking)
                .TrackingSets(TrackingElement.Mouth, VRC_AnimatorTrackingControl.TrackingType.Tracking);

            var afkEnterState = afkStateMachine.NewState("AFK Enter", 0, 2)
                .Drives(layer.BoolParameter(AV3Constants.ParamName_CN_BLINK_ENABLE), true)
                .Drives(layer.BoolParameter(AV3Constants.ParamName_CN_MOUTH_MORPH_CANCEL_ENABLE), true)
                .TrackingSets(TrackingElement.Eyes, VRC_AnimatorTrackingControl.TrackingType.Tracking)
                .TrackingSets(TrackingElement.Mouth, VRC_AnimatorTrackingControl.TrackingType.Tracking);
            afkStandbyState.TransitionsTo(afkEnterState)
                .WithTransitionDurationSeconds(0.1f)
                .When(layer.BoolParameter(AV3Constants.ParamName_Dummy).IsFalse());
            if (aV3Setting.AfkEnterFace != null && aV3Setting.AfkEnterFace != null) { afkEnterState.WithAnimation(aV3Setting.AfkEnterFace); }

            var afkState = afkStateMachine.NewState("AFK", 0, 3)
                .Drives(layer.BoolParameter(AV3Constants.ParamName_CN_BLINK_ENABLE), false)
                .Drives(layer.BoolParameter(AV3Constants.ParamName_CN_MOUTH_MORPH_CANCEL_ENABLE), false)
                .TrackingSets(TrackingElement.Eyes, VRC_AnimatorTrackingControl.TrackingType.Animation)
                .TrackingSets(TrackingElement.Mouth, VRC_AnimatorTrackingControl.TrackingType.Tracking);
            afkEnterState.TransitionsTo(afkState)
                .WithTransitionDurationSeconds(0.75f)
                .AfterAnimationFinishes();
            if (aV3Setting.AfkFace != null && aV3Setting.AfkFace != null) { afkState.WithAnimation(aV3Setting.AfkFace); }

            var afkExitState = afkStateMachine.NewState("AFK Exit", 0, 4)
                .Drives(layer.BoolParameter(AV3Constants.ParamName_CN_BLINK_ENABLE), true)
                .Drives(layer.BoolParameter(AV3Constants.ParamName_CN_MOUTH_MORPH_CANCEL_ENABLE), true)
                .TrackingSets(TrackingElement.Eyes, VRC_AnimatorTrackingControl.TrackingType.Tracking)
                .TrackingSets(TrackingElement.Mouth, VRC_AnimatorTrackingControl.TrackingType.Tracking);
            afkState.TransitionsTo(afkExitState)
                .WithTransitionDurationSeconds(0.75f)
                .When(layer.Av3().AFK.IsFalse());
            afkExitState.Exits()
                .WithTransitionDurationSeconds(0.1f)
                .AfterAnimationFinishes();
            if (aV3Setting.AfkExitFace != null && aV3Setting.AfkExitFace != null) { afkExitState.WithAnimation(aV3Setting.AfkExitFace); }

            // Create override state
            var overrideState = layer.NewState("in OVERRIDE", 2, -1);
            overrideState.TransitionsFromAny()
                .When(layer.BoolParameter(AV3Constants.ParamName_CN_EMOTE_OVERRIDE).IsTrue())
                .And(layer.BoolParameter(AV3Constants.ParamName_SYNC_CN_DANCE_GIMMICK_ENABLE).IsFalse())
                .Or()
                .When(layer.BoolParameter(AV3Constants.ParamName_CN_EMOTE_OVERRIDE).IsTrue())
                .And(layer.Av3().InStation.IsFalse());
            overrideState.Exits()
                .When(layer.BoolParameter(AV3Constants.ParamName_CN_EMOTE_OVERRIDE).IsFalse());

            // Create dance state
            var danceState = layer.NewState("in DANCE", 3, -1);
            danceState.TransitionsFromAny()
                .When(layer.BoolParameter(AV3Constants.ParamName_SYNC_CN_DANCE_GIMMICK_ENABLE).IsTrue())
                .And(layer.Av3().InStation.IsTrue())
                .And(layer.Av3().Voice.IsLessThan(AV3Constants.VoiceThreshold));
            danceState.Exits()
                .When(layer.BoolParameter(AV3Constants.ParamName_SYNC_CN_DANCE_GIMMICK_ENABLE).IsFalse())
                .Or()
                .When(layer.Av3().InStation.IsFalse());

            EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Generating \"{layerName}\" layer...", 1);
        }

        private void ModifyBlinkLayer(AacFlBase aac, VRCAvatarDescriptor avatarDescriptor, AnimatorController animatorController)
        {
            var layerName = AV3Constants.LayerName_Blink;

            EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Modifying \"{layerName}\" layer...", 0);

            AacFlClip motion;
            if (_aV3Setting.ReplaceBlink)
            {
                motion = GetBlinkAnimation(aac, avatarDescriptor);
            }
            else
            {
                motion = aac.NewClip();
            }
            AV3Utility.SetMotion(animatorController, layerName, AV3Constants.StateName_BlinkEnabled, motion.Clip);

            EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Modifying \"{layerName}\" layer...", 1);
        }

        private void ModifyMouthMorphCancelerLayer(AV3Setting aV3Setting, AacFlBase aac, VRCAvatarDescriptor avatarDescriptor, AnimatorController animatorController)
        {
            var layerName = AV3Constants.LayerName_MouthMorphCanceler;

            EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Modifying \"{layerName}\" layer...", 0);

            var motion = GetMouthMorphCancelerAnimation(aV3Setting, aac, avatarDescriptor);
            AV3Utility.SetMotion(animatorController, layerName, AV3Constants.StateName_MouthMorphCancelerEnabled, motion.Clip);

            EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Modifying \"{layerName}\" layer...", 1);
        }

        private VRCExpressionsMenu GenerateExMenu(IReadOnlyList<ModeEx> modes, IMenu menu, string exMenuPath, bool useOverLimitMode)
        {
            var loc = _localizationSetting.GetCurrentLocaleTable();

            AssetDatabase.DeleteAsset(exMenuPath);
            var container = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            AssetDatabase.CreateAsset(container, exMenuPath);

            var idToModeIndex = new Dictionary<string, int>();
            var idToModeEx = new Dictionary<string, ModeEx>();
            for (int modeIndex = 0; modeIndex < modes.Count; modeIndex++)
            {
                var mode = modes[modeIndex];
                var id = mode.Mode.GetId();

                idToModeIndex[id] = modeIndex;
                idToModeEx[id] = mode;
            }

            var rootMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            rootMenu.name = AV3Constants.RootMenuName;

            // Re-generate thumbnails
            if (_aV3Setting.GenerateExMenuThumbnails)
            {
                _exMenuThumbnailDrawer.ClearCache();
                foreach (var mode in modes)
                {
                    _exMenuThumbnailDrawer.GetThumbnail(mode.Mode.Animation);
                    if (_aV3Setting.AddConfig_EmoteSelect)
                    {
                        foreach (var branch in mode.Mode.Branches)
                        {
                            _exMenuThumbnailDrawer.GetThumbnail(branch.BaseAnimation);
                        }
                    }
                }
                _exMenuThumbnailDrawer.Update();
            }

            // Get icons
            var folderIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath("a06282136d558c54aa15d533f163ff59")); // item folder
            var logo = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath("617fecc28d6cb5a459d1297801b9213e")); // logo
            var lockIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(AV3Constants.Path_BearsDenIcons + "/Lock.png");

            // Mode select
            GenerateSubMenuRecursive(rootMenu, menu.Registered, idToModeIndex, container);

            // Emote select
            if (_aV3Setting.AddConfig_EmoteSelect)
            {
                var emoteSelectMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                emoteSelectMenu.name = loc.ExMenu_EmoteSelect;

                emoteSelectMenu.controls.Add(CreateBoolToggleControl(loc.ExMenu_EmoteLock, AV3Constants.ParamName_CN_EMOTE_LOCK_ENABLE, lockIcon));

                GenerateEmoteSelectMenuRecursive(emoteSelectMenu, menu.Registered, idToModeIndex, container, idToModeEx, useOverLimitMode);

                var emoteSelectControl = CreateSubMenuControl(loc.ExMenu_EmoteSelect, emoteSelectMenu, folderIcon);
                emoteSelectControl.parameter = new VRCExpressionsMenu.Control.Parameter() { name = AV3Constants.ParamName_CN_EMOTE_PRELOCK_ENABLE };
                emoteSelectControl.value = 1;
                rootMenu.controls.Add(emoteSelectControl);

                AssetDatabase.AddObjectToAsset(emoteSelectMenu, container);
            }
            else
            {
                rootMenu.controls.Add(CreateBoolToggleControl(loc.ExMenu_EmoteLock, AV3Constants.ParamName_CN_EMOTE_LOCK_ENABLE, lockIcon));
            }

            // Setting
            GenerateSettingMenu(rootMenu, container);

            container.controls.Add(CreateSubMenuControl(AV3Constants.RootMenuName, rootMenu, logo));
            AssetDatabase.AddObjectToAsset(rootMenu, container);

            EditorUtility.SetDirty(container);

            return container;
        }

        private void GenerateSubMenuRecursive(VRCExpressionsMenu parent, IMenuItemList menuItemList, Dictionary<string, int> idToModeIndex, VRCExpressionsMenu container)
        {
            foreach (var id in menuItemList.Order)
            {
                var type = menuItemList.GetType(id);
                if (type == MenuItemType.Mode)
                {
                    EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Generating pattern selection controls...", (float)idToModeIndex[id] / idToModeIndex.Count);

                    var mode = new ModeExInner(menuItemList.GetMode(id));
                    var control = CreateIntToggleControl(_modeNameProvider.Provide(mode), AV3Constants.ParamName_EM_EMOTE_PATTERN, idToModeIndex[id], icon: null);

                    Texture2D icon = null;
                    if (_aV3Setting.GenerateExMenuThumbnails && mode.ChangeDefaultFace)
                    {
                        icon = GetExpressionThumbnail(mode.Animation, container);
                    }
                    else
                    {
                        icon = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath("af1ba8919b0ccb94a99caf43ac36f97d")); // face smile
                    }
                    control.icon = icon;

                    parent.controls.Add(control);
                }
                else
                {
                    var group = menuItemList.GetGroup(id);

                    var subMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                    subMenu.name = group.DisplayName;
                    var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath("a06282136d558c54aa15d533f163ff59")); // item folder
                    parent.controls.Add(CreateSubMenuControl(group.DisplayName, subMenu, icon));

                    GenerateSubMenuRecursive(subMenu, group, idToModeIndex, container);
                    AssetDatabase.AddObjectToAsset(subMenu, container);
                }
            }
        }

        private Texture2D GetExpressionThumbnail(Domain.Animation animation, VRCExpressionsMenu container)
        {
            var icon = _exMenuThumbnailDrawer.GetThumbnail(animation);
            if (!AssetDatabase.IsMainAsset(icon) && !AssetDatabase.IsSubAsset(icon)) // Do not save icons that have already been generated and error icons
            {
                AssetDatabase.AddObjectToAsset(icon, container);
                AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(container));
            }
            return icon;
        }

        private void GenerateEmoteSelectMenuRecursive(VRCExpressionsMenu parent, IMenuItemList menuItemList, Dictionary<string, int> idToModeIndex, VRCExpressionsMenu container,
            Dictionary<string, ModeEx> idToModeEx, bool useOverLimitMode)
        {
            var loc = _localizationSetting.GetCurrentLocaleTable();

            foreach (var id in menuItemList.Order)
            {
                var type = menuItemList.GetType(id);
                if (type == MenuItemType.Mode)
                {
                    EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Generating emote selection controls...", (float)idToModeIndex[id] / idToModeIndex.Count);

                    // Get branches
                    var mode = new ModeExInner(menuItemList.GetMode(id));
                    var numOfBranches = mode.Branches.Count;
                    if (mode.ChangeDefaultFace) { numOfBranches++; }
                    if (numOfBranches <= 0) { continue; }

                    // Create mode folder
                    var modeFolder = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                    modeFolder.name = _modeNameProvider.Provide(mode);
                    var folderIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath("a06282136d558c54aa15d533f163ff59")); // item folder
                    var modeControl = CreateSubMenuControl(_modeNameProvider.Provide(mode), modeFolder, folderIcon);
                    if (useOverLimitMode)
                    {
                        modeControl.parameter = new VRCExpressionsMenu.Control.Parameter() { name = AV3Constants.ParamName_EM_EMOTE_PATTERN };
                        modeControl.value = idToModeIndex[id];
                    }
                    parent.controls.Add(modeControl);

                    // Calculate num of branch folders
                    const int itemLimit = 8;
                    var numOfBranchFolders = numOfBranches / itemLimit;
                    if (numOfBranches % itemLimit != 0) { numOfBranchFolders++; }
                    numOfBranchFolders = Math.Min(numOfBranchFolders, itemLimit);

                    for (int folderIndex = 0; folderIndex < numOfBranchFolders; folderIndex++)
                    {
                        var startBranchIndex = folderIndex * itemLimit;
                        var endBranchIndex = (folderIndex + 1) * itemLimit - 1;

                        // Create branch folder
                        var branchFolder = modeFolder;
                        var createFolder = numOfBranchFolders > 1;
                        if (createFolder)
                        {
                            var branchFolderName = $"{startBranchIndex + 1} - {Math.Min(endBranchIndex + 1, numOfBranches)}";
                            branchFolder = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                            branchFolder.name = branchFolderName;
                            modeFolder.controls.Add(CreateSubMenuControl(branchFolderName, branchFolder, folderIcon));
                        }

                        // Create branch controls
                        if (mode.ChangeDefaultFace)
                        {
                            startBranchIndex--;
                            endBranchIndex--;
                        }
                        for (int branchIndex = startBranchIndex; branchIndex <= endBranchIndex; branchIndex++)
                        {
                            Domain.Animation guid;
                            // Mode
                            if (branchIndex < 0)
                            {
                                guid = mode.Animation;
                            }
                            // Branch
                            else
                            {
                                if (branchIndex >= mode.Branches.Count) { continue; }
                                guid = mode.Branches[branchIndex].BaseAnimation;
                            }
                            var emoteIndex = GetEmoteIndex(branchIndex, idToModeEx[id], useOverLimitMode);
                            var animation = AV3Utility.GetAnimationClipWithName(guid);
                            var animationName = !string.IsNullOrEmpty(animation.name) ? animation.name : loc.ModeNameProvider_NoExpression;

                            Texture2D emoteIcon = null;
                            if (_aV3Setting.GenerateExMenuThumbnails)
                            {
                                emoteIcon = GetExpressionThumbnail(guid, container);
                            }
                            else
                            {
                                emoteIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath("af1ba8919b0ccb94a99caf43ac36f97d")); // face smile
                            }

                            var control = CreateIntToggleControl(animationName, AV3Constants.ParamName_SYNC_EM_EMOTE, emoteIndex, icon: emoteIcon);
                            branchFolder.controls.Add(control);
                        }

                        if (createFolder) { AssetDatabase.AddObjectToAsset(branchFolder, container); }
                    }
                    AssetDatabase.AddObjectToAsset(modeFolder, container);
                }
                else
                {
                    var group = menuItemList.GetGroup(id);

                    var subMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                    subMenu.name = group.DisplayName;
                    var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath("a06282136d558c54aa15d533f163ff59")); // item folder
                    parent.controls.Add(CreateSubMenuControl(group.DisplayName, subMenu, icon));

                    GenerateEmoteSelectMenuRecursive(subMenu, group, idToModeIndex, container, idToModeEx, useOverLimitMode);
                    AssetDatabase.AddObjectToAsset(subMenu, container);
                }
            }
        }

        private void GenerateSettingMenu(VRCExpressionsMenu parent, VRCExpressionsMenu container)
        {
            EditorUtility.DisplayProgressBar(DomainConstants.SystemName, $"Generating setting menu...", 0);

            var loc = _localizationSetting.GetCurrentLocaleTable();

            var settingRoot = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            settingRoot.name = loc.ExMenu_Setting;

            // Blink off setting
            if (_aV3Setting.AddConfig_BlinkOff && _aV3Setting.ReplaceBlink)
            {
                var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(AV3Constants.Path_BearsDenIcons + "/BlinkOff.png");
                settingRoot.controls.Add(CreateBoolToggleControl(loc.ExMenu_BlinkOff,  AV3Constants.ParamName_SYNC_CN_FORCE_BLINK_DISABLE, icon));
            }

            // Dance gimmick setting
            if (_aV3Setting.AddConfig_DanceGimmick)
            {
                var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath("9a20b3a6641e1af4e95e058f361790cb")); // person dance
                settingRoot.controls.Add(CreateBoolToggleControl(loc.ExMenu_DanceGimmick, AV3Constants.ParamName_SYNC_CN_DANCE_GIMMICK_ENABLE, icon));
            }

            // Contact emote lock setting
            if (_aV3Setting.AddConfig_ContactLock)
            {
                var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(AV3Constants.Path_BearsDenIcons + "/ContactLock.png");
                settingRoot.controls.Add(CreateBoolToggleControl(loc.ExMenu_ContactLock, AV3Constants.ParamName_CN_CONTACT_EMOTE_LOCK_ENABLE, icon));
            }

            // Emote override setting
            if (_aV3Setting.AddConfig_Override)
            {
                var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath("5acca5d9b1a37724880f1a1dc1bc54d3")); // hand waving
                settingRoot.controls.Add(CreateBoolToggleControl(loc.ExMenu_Override, AV3Constants.ParamName_SYNC_CN_EMOTE_OVERRIDE_ENABLE, icon));
            }

            // Wait emote by voice setting
            if (_aV3Setting.AddConfig_Voice)
            {
                var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath("50865644fb00f2b4d88bf8a8186039f5")); // face gasp
                settingRoot.controls.Add(CreateBoolToggleControl(loc.ExMenu_Voice, AV3Constants.ParamName_SYNC_CN_WAIT_FACE_EMOTE_BY_VOICE, icon));
            }

            // Hand pattern setting
            if (_aV3Setting.AddConfig_HandPattern_Swap || _aV3Setting.AddConfig_HandPattern_DisableLeft || _aV3Setting.AddConfig_HandPattern_DisableRight)
            {
                var swapIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(AV3Constants.Path_BearsDenIcons + "/HandRL.png");
                var leftDisableIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(AV3Constants.Path_BearsDenIcons + "/Additional/HandL_disable.png");
                var rightDisableIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(AV3Constants.Path_BearsDenIcons + "/Additional/HandR_disable.png");

                var handPatternSetting = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                handPatternSetting.name = loc.ExMenu_HandPattern;
                handPatternSetting.controls = new List<VRCExpressionsMenu.Control>();
                if (_aV3Setting.AddConfig_HandPattern_Swap) { handPatternSetting.controls.Add(CreateBoolToggleControl(loc.ExMenu_HandPattern_SwapLR, AV3Constants.ParamName_CN_EMOTE_SELECT_SWAP_LR, swapIcon)); }
                if (_aV3Setting.AddConfig_HandPattern_DisableLeft) { handPatternSetting.controls.Add(CreateBoolToggleControl(loc.ExMenu_HandPattern_DisableLeft, AV3Constants.ParamName_CN_EMOTE_SELECT_DISABLE_LEFT, leftDisableIcon)); }
                if (_aV3Setting.AddConfig_HandPattern_DisableRight) { handPatternSetting.controls.Add(CreateBoolToggleControl(loc.ExMenu_HandPattern_DisableRight, AV3Constants.ParamName_CN_EMOTE_SELECT_DISABLE_RIGHT, rightDisableIcon)); }

                var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(AV3Constants.Path_BearsDenIcons + "/FaceSelect.png");
                settingRoot.controls.Add(CreateSubMenuControl(loc.ExMenu_HandPattern, handPatternSetting, icon));
                AssetDatabase.AddObjectToAsset(handPatternSetting, container);
            }

            // Controller setting
            if (_aV3Setting.AddConfig_Controller_Quest || _aV3Setting.AddConfig_Controller_Index)
            {
                var questIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(AV3Constants.Path_BearsDenIcons + "/QuestController.png");
                var indexIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(AV3Constants.Path_BearsDenIcons + "/IndexController.png");

                var controllerSetting = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                controllerSetting.name = loc.ExMenu_Controller;
                controllerSetting.controls = new List<VRCExpressionsMenu.Control>();
                if (_aV3Setting.AddConfig_Controller_Quest) { controllerSetting.controls.Add(CreateBoolToggleControl(loc.ExMenu_Controller_Quest, AV3Constants.ParamName_CN_CONTROLLER_TYPE_QUEST, questIcon)); }
                if (_aV3Setting.AddConfig_Controller_Index) { controllerSetting.controls.Add(CreateBoolToggleControl(loc.ExMenu_Controller_Index, AV3Constants.ParamName_CN_CONTROLLER_TYPE_INDEX, indexIcon)); }

                var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(AV3Constants.Path_BearsDenIcons + "/Controller.png");
                settingRoot.controls.Add(CreateSubMenuControl(loc.ExMenu_Controller, controllerSetting, icon));
                AssetDatabase.AddObjectToAsset(controllerSetting, container);
            }

            var settingIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(AV3Constants.Path_BearsDenIcons + "/Settings.png");
            parent.controls.Add(CreateSubMenuControl(loc.ExMenu_Setting, settingRoot, settingIcon));
            AssetDatabase.AddObjectToAsset(settingRoot, container);
        }

        private static VRCExpressionsMenu.Control CreateBoolToggleControl(string name, string parameterName, Texture2D icon)
        {
            var control = new VRCExpressionsMenu.Control();
            control.name = name;
            control.type = VRCExpressionsMenu.Control.ControlType.Toggle;
            control.parameter = new VRCExpressionsMenu.Control.Parameter() { name = parameterName };
            control.subParameters = new VRCExpressionsMenu.Control.Parameter[0];
            control.icon = icon;
            control.labels = new VRCExpressionsMenu.Control.Label[0];
            return control;
        }

        private static VRCExpressionsMenu.Control CreateIntToggleControl(string name, string parameterName, int value, Texture2D icon)
        {
            var control = CreateBoolToggleControl(name, parameterName, icon);
            control.value = value;
            return control;
        }

        private static VRCExpressionsMenu.Control CreateSubMenuControl(string name, VRCExpressionsMenu subMenu, Texture2D icon)
        {
            var control = new VRCExpressionsMenu.Control();
            control.name = name;
            control.type = VRCExpressionsMenu.Control.ControlType.SubMenu;
            control.parameter = new VRCExpressionsMenu.Control.Parameter() { name = string.Empty };
            control.subParameters = new VRCExpressionsMenu.Control.Parameter[0];
            control.subMenu = subMenu;
            control.icon = icon;
            control.labels = new VRCExpressionsMenu.Control.Label[0];
            return control;
        }

        private static GameObject GetMARootObject(VRCAvatarDescriptor avatarDescriptor)
        {
            var avatarRoot = avatarDescriptor.gameObject;
            if (avatarRoot == null)
            {
                return null;
            }

            var rootObject = avatarRoot.transform.Find(AV3Constants.MARootObjectName)?.gameObject;
            if (rootObject == null)
            {
                rootObject = new GameObject(AV3Constants.MARootObjectName);
                rootObject.transform.parent = avatarRoot.transform;
            }

            return rootObject;
        }

        private static void InstantiatePrefabs(GameObject rootObject)
        {
            var paths = new[] { AV3Constants.Path_EmoteLocker, AV3Constants.Path_IndicatorSound, };
            foreach (var path in paths)
            {
                var prefabName = Path.GetFileNameWithoutExtension(path);
                var existing = rootObject.transform.Find(prefabName)?.gameObject;
                if (existing != null)
                {
                    UnityEngine.Object.DestroyImmediate(existing);
                }

                var loaded = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (loaded == null)
                {
                    throw new FacialExpressionSwitcherException($"Failed to load prefab: {path}");
                }

                var instantiated = PrefabUtility.InstantiatePrefab(loaded) as GameObject;
                instantiated.transform.parent = rootObject.transform;
                instantiated.transform.SetAsFirstSibling();
            }
        }

        private static void AddMergeAnimatorComponent(GameObject rootObject, AnimatorController animatorController)
        {
            foreach (var component in rootObject.GetComponents<ModularAvatarMergeAnimator>())
            {
                UnityEngine.Object.DestroyImmediate(component);
            }
            var modularAvatarMergeAnimator = rootObject.AddComponent<ModularAvatarMergeAnimator>();

            modularAvatarMergeAnimator.animator = animatorController;
            modularAvatarMergeAnimator.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
            modularAvatarMergeAnimator.deleteAttachedAnimator = true;
            modularAvatarMergeAnimator.pathMode = MergeAnimatorPathMode.Absolute;
            modularAvatarMergeAnimator.matchAvatarWriteDefaults = false;

            EditorUtility.SetDirty(modularAvatarMergeAnimator);
        }

        private static void AddMenuInstallerComponent(GameObject rootObject, VRCExpressionsMenu expressionsMenu)
        {
            foreach (var component in rootObject.GetComponents<ModularAvatarMenuInstaller>())
            {
                UnityEngine.Object.DestroyImmediate(component);
            }
            var modularAvatarMenuInstaller = rootObject.AddComponent<ModularAvatarMenuInstaller>();

            modularAvatarMenuInstaller.menuToAppend = expressionsMenu;

            EditorUtility.SetDirty(modularAvatarMenuInstaller);
        }

        private void AddParameterComponent(GameObject rootObject, int defaultModeIndex)
        {
            foreach (var component in rootObject.GetComponents<ModularAvatarParameters>())
            {
                UnityEngine.Object.DestroyImmediate(component);
            }
            var modularAvatarParameters = rootObject.AddComponent<ModularAvatarParameters>();

            // Config (Saved) (Bool)
            var contactLockEnabled = _aV3Setting.AddConfig_ContactLock;
            modularAvatarParameters.parameters.Add(MAParam(AV3Constants.ParamName_CN_CONTROLLER_TYPE_QUEST,         _aV3Setting.AddConfig_Controller_Quest ? Sync.Bool : Sync.NotSynced,            defaultValue: _aV3Setting.DefaultValue_Controller_Quest ? 1 : 0,            saved: true, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(MAParam(AV3Constants.ParamName_CN_CONTROLLER_TYPE_INDEX,         _aV3Setting.AddConfig_Controller_Index ? Sync.Bool : Sync.NotSynced,            defaultValue: _aV3Setting.DefaultValue_Controller_Index ? 1 : 0,            saved: true, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(MAParam(AV3Constants.ParamName_CN_EMOTE_SELECT_SWAP_LR,          _aV3Setting.AddConfig_HandPattern_Swap ? Sync.Bool : Sync.NotSynced,            defaultValue: _aV3Setting.DefaultValue_HandPattern_Swap ? 1 : 0,            saved: true, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(MAParam(AV3Constants.ParamName_CN_EMOTE_SELECT_DISABLE_LEFT,     _aV3Setting.AddConfig_HandPattern_DisableLeft ? Sync.Bool : Sync.NotSynced,     defaultValue: _aV3Setting.DefaultValue_HandPattern_DisableLeft ? 1 : 0,     saved: true, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(MAParam(AV3Constants.ParamName_CN_EMOTE_SELECT_DISABLE_RIGHT,    _aV3Setting.AddConfig_HandPattern_DisableRight ? Sync.Bool : Sync.NotSynced,    defaultValue: _aV3Setting.DefaultValue_HandPattern_DisableRight ? 1 : 0,    saved: true, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(MAParam(AV3Constants.ParamName_CN_CONTACT_EMOTE_LOCK_ENABLE,     contactLockEnabled ? Sync.Bool : Sync.NotSynced,                                defaultValue: _aV3Setting.DefaultValue_ContactLock ? 1 : 0,                 saved: true, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(MAParam(AV3Constants.ParamName_SYNC_CN_EMOTE_OVERRIDE_ENABLE,    _aV3Setting.AddConfig_Override ? Sync.Bool : Sync.NotSynced,                    defaultValue: _aV3Setting.DefaultValue_Override ? 1 : 0,                    saved: true, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(MAParam(AV3Constants.ParamName_SYNC_CN_WAIT_FACE_EMOTE_BY_VOICE, _aV3Setting.AddConfig_Voice ? Sync.Bool : Sync.NotSynced,                       defaultValue: _aV3Setting.DefaultValue_Voice ? 1 : 0,                       saved: true, addPrefix: _aV3Setting.AddParameterPrefix));

            // Config (Saved) (Int)
            modularAvatarParameters.parameters.Add(MAParam(AV3Constants.ParamName_EM_EMOTE_PATTERN, Sync.Int, defaultValue: defaultModeIndex, saved: true, addPrefix: _aV3Setting.AddParameterPrefix));

            // Config (Not saved) (Bool)
            var blinkOffEnabled = _aV3Setting.AddConfig_BlinkOff && _aV3Setting.ReplaceBlink;
            modularAvatarParameters.parameters.Add(MAParam(AV3Constants.ParamName_CN_EMOTE_LOCK_ENABLE,         Sync.Bool,       defaultValue: 0, saved: false, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(MAParam(AV3Constants.ParamName_CN_EMOTE_PRELOCK_ENABLE,      _aV3Setting.AddConfig_EmoteSelect ? Sync.Bool : Sync.NotSynced,     defaultValue: 0, saved: false, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(MAParam(AV3Constants.ParamName_SYNC_CN_FORCE_BLINK_DISABLE,  blinkOffEnabled ? Sync.Bool : Sync.NotSynced,                       defaultValue: 0, saved: false, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(MAParam(AV3Constants.ParamName_SYNC_CN_DANCE_GIMMICK_ENABLE, _aV3Setting.AddConfig_DanceGimmick ? Sync.Bool : Sync.NotSynced,    defaultValue: 0, saved: false, addPrefix: _aV3Setting.AddParameterPrefix));

            // Synced (Int)
            modularAvatarParameters.parameters.Add(MAParam(AV3Constants.ParamName_SYNC_EM_EMOTE, Sync.Int, defaultValue: 0, saved: false, addPrefix: _aV3Setting.AddParameterPrefix));

            // Not synced
            modularAvatarParameters.parameters.Add(NotSyncedMAParam(AV3Constants.ParamName_Dummy, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(NotSyncedMAParam(AV3Constants.ParamName_EM_EMOTE_SELECT_L, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(NotSyncedMAParam(AV3Constants.ParamName_EM_EMOTE_SELECT_R, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(NotSyncedMAParam(AV3Constants.ParamName_EM_EMOTE_PRESELECT, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(NotSyncedMAParam(AV3Constants.ParamName_CN_BLINK_ENABLE, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(NotSyncedMAParam(AV3Constants.ParamName_CN_MOUTH_MORPH_CANCEL_ENABLE, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(NotSyncedMAParam(AV3Constants.ParamName_CN_EMOTE_OVERRIDE, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(NotSyncedMAParam(AV3Constants.ParamName_EV_PLAY_INDICATOR_SOUND, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(NotSyncedMAParam(AV3Constants.ParamName_CNST_TOUCH_NADENADE_POINT, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(NotSyncedMAParam(AV3Constants.ParamName_CNST_TOUCH_EMOTE_LOCK_TRIGGER_L, addPrefix: _aV3Setting.AddParameterPrefix));
            modularAvatarParameters.parameters.Add(NotSyncedMAParam(AV3Constants.ParamName_CNST_TOUCH_EMOTE_LOCK_TRIGGER_R, addPrefix: _aV3Setting.AddParameterPrefix));

            EditorUtility.SetDirty(modularAvatarParameters);
        }

        private int GetDefaultModeIndex(IReadOnlyList<ModeEx> modes, IMenu menu)
        {
            int defaultModeIndex = 0;
            for (int modeIndex = 0; modeIndex < modes.Count; modeIndex++)
            {
                var mode = modes[modeIndex];
                var id = mode.Mode.GetId();
                if (id == menu.DefaultSelection)
                {
                    defaultModeIndex = modeIndex;
                    break;
                }
            }
            return defaultModeIndex;
        }

        private static ParameterConfig MAParam(string name, ParameterSyncType type, float defaultValue, bool saved, bool addPrefix) 
        {
            var parameterConfig = new ParameterConfig();

            parameterConfig.nameOrPrefix = name;
            parameterConfig.syncType = type;
            if (addPrefix) { parameterConfig.remapTo = AV3Constants.ParameterPrefix + name; }
            if (type != Sync.NotSynced)
            {
                parameterConfig.defaultValue = defaultValue;
                parameterConfig.saved = saved;
            }

            return parameterConfig;
        }

        private static ParameterConfig NotSyncedMAParam(string name, bool addPrefix) => MAParam(name, Sync.NotSynced, defaultValue: 0, saved: false, addPrefix: addPrefix);

        private void AddBlinkDisablerComponent(GameObject rootObject)
        {
            foreach (var component in rootObject.GetComponents<BlinkDisabler>())
            {
                UnityEngine.Object.DestroyImmediate(component);
            }

            if (_aV3Setting.ReplaceBlink)
            {
                rootObject.AddComponent<BlinkDisabler>();
            }
        }

        private void AddTrackingControlDisablerComponent(GameObject rootObject)
        {
            foreach (var component in rootObject.GetComponents<TrackingControlDisabler>())
            {
                UnityEngine.Object.DestroyImmediate(component);
            }

            if (_aV3Setting.DisableTrackingControls)
            {
                rootObject.AddComponent<TrackingControlDisabler>();
            }
        }

        private AacFlClip GetDefaultFaceAnimation(AacFlBase aac, VRCAvatarDescriptor avatarDescriptor)
        {
            var loc = _localizationSetting.GetCurrentLocaleTable();

            var clip = aac.NewClip();

            // Generate fesh mesh blendshape animation
            var faceMesh = AV3Utility.GetFaceMesh(avatarDescriptor);
            if (faceMesh != null)
            {
                var excludeBlink = !_aV3Setting.ReplaceBlink; // If blinking is not replaced by animation, do not reset the shape key for blinking
                var excludeLipSync = true;
                var blendShapes = AV3Utility.GetFaceMeshBlendShapes(avatarDescriptor, excludeBlink, excludeLipSync);
                foreach (var name in blendShapes.Keys)
                {
                    var weight = blendShapes[name];
                    clip = clip.BlendShape(faceMesh, name, weight);
                }
            }
            else
            {
                Debug.LogError(loc.FxGenerator_Message_FaceMeshNotFound);
            }

            // Generate additional expresstion objects animation
            foreach (var gameObject in _aV3Setting.AdditionalToggleObjects)
            {
                if (gameObject != null)
                {
                    clip = clip.Toggling(new[] { gameObject }, gameObject.activeSelf);
                }
            }

            foreach (var gameObject in _aV3Setting.AdditionalTransformObjects)
            {
                if (gameObject != null)
                {
                    clip = clip.Positioning(new[] { gameObject }, gameObject.transform.localPosition);
                    clip = clip.Rotationing(new[] { gameObject }, gameObject.transform.localEulerAngles);
                    clip = clip.Scaling(new[] { gameObject }, gameObject.transform.localScale);
                }
            }

            return clip;
        }

        private AacFlClip GetBlinkAnimation(AacFlBase aac, VRCAvatarDescriptor avatarDescriptor)
        {
            var loc = _localizationSetting.GetCurrentLocaleTable();

            var clip = aac.NewClip().Looping();

            var faceMesh = AV3Utility.GetFaceMesh(avatarDescriptor);
            if (faceMesh == null)
            {
                Debug.LogError(loc.FxGenerator_Message_FaceMeshNotFound);
                return clip;
            }

            var eyeLids = AV3Utility.GetEyeLidsBlendShapes(avatarDescriptor);
            if (eyeLids.Count > 0)
            {
                clip = clip.BlendShape(faceMesh, eyeLids.First(), GetBlinkCurve());
            }
            else
            {
                Debug.LogError(loc.FxGenerator_Message_BlinkBlendShapeNotFound);
            }

            return clip;
        }

        private static AnimationCurve GetBlinkCurve()
        {
            const string blendShapePrefix = "blendShape.";

            var template = AssetDatabase.LoadAssetAtPath<AnimationClip>(AV3Constants.Path_BlinkTemplate);
            if (template == null)
            {
                throw new FacialExpressionSwitcherException("Blink template was not found.");
            }

            var bindings = AnimationUtility.GetCurveBindings(template);
            if (bindings.Count() == 1 && bindings.First().propertyName == blendShapePrefix + "blink")
            {
                var binding = bindings.First();
                return AnimationUtility.GetEditorCurve(template, binding);
            }
            else
            {
                throw new FacialExpressionSwitcherException("Invalid blink template count.");
            }
        }

        private AacFlClip GetMouthMorphCancelerAnimation(AV3Setting aV3Setting, AacFlBase aac, VRCAvatarDescriptor avatarDescriptor)
        {
            var clip = aac.NewClip();

            var faceMesh = AV3Utility.GetFaceMesh(avatarDescriptor);
            if (faceMesh == null)
            {
                return clip;
            }

            // Generate clip
            var mouthMorphBlendShapes = new HashSet<string>(aV3Setting.MouthMorphBlendShapes);
            var excludeBlink = !_aV3Setting.ReplaceBlink; // If blinking is not replaced by animation, do not reset the shape key for blinking
            var excludeLipSync = true;
            var blendShapes = AV3Utility.GetFaceMeshBlendShapes(avatarDescriptor, excludeBlink, excludeLipSync);
            foreach (var name in blendShapes.Keys)
            {
                var weight = blendShapes[name];
                if (mouthMorphBlendShapes.Contains(name))
                {
                    clip = clip.BlendShape(faceMesh, name, weight);
                }
            }

            return clip;
        }

        private static AacConfiguration GetConfiguration(VRCAvatarDescriptor avatarDescriptor, AnimatorController animatorController, bool writeDefaults)
        {
            return new AacConfiguration() {
                SystemName = string.Empty,
                AvatarDescriptor = null,
                AnimatorRoot = avatarDescriptor.transform,
                DefaultValueRoot = avatarDescriptor.transform,
                AssetContainer = animatorController,
                AssetKey = "FES",
                DefaultsProvider = new FesAacDefaultsProvider(writeDefaults),
            };
        }

        private static int GetEmoteCount(IReadOnlyList<ModeEx> modes)
        {
            if (modes is null || modes.Count == 0)
            {
                return 0;
            }
            else
            {
                var last = modes.Last();
                return last.DefaultEmoteIndex + last.Mode.Branches.Count + 1;
            }
        }

        // TODO: Refactor domain method
        // Should invalid value -1?
        private static int GetBranchIndex(HandGesture left, HandGesture right, IMode mode)
        {
            var branch = mode.GetGestureCell(left, right);
            if (branch is null)
            {
                return -1;
            }

            for (int i = 0; i < mode.Branches.Count; i++)
            {
                if (ReferenceEquals(branch, mode.Branches[i]))
                {
                    return i;
                }
            }

            return - 1;
        }

        private static int GetEmoteIndex(int branchIndex, ModeEx mode, bool useOverLimitMode)
        {
            if (useOverLimitMode)
            {
                if (branchIndex < 0) { return 0; }
                else { return branchIndex + 1; }
            }
            else
            {
                if (branchIndex < 0) { return mode.DefaultEmoteIndex; }
                else { return mode.DefaultEmoteIndex + branchIndex + 1; }
            }
        }

        private static void CleanAssets()
        {
            // To include inactive objects, Resources.FindObjectsOfTypeAll<T>() must be used in Unity 2019.
            var referencedFxGUIDs = new HashSet<string>();
            foreach (var anim in Resources.FindObjectsOfTypeAll<ModularAvatarMergeAnimator>())
            {
                referencedFxGUIDs.Add(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(anim.animator)));
            }

            // Delete dateDir which is not referenced.
            foreach (var dateDir in AssetDatabase.GetSubFolders(AV3Constants.Path_GeneratedDir))
            {
                var referenced = false;
                foreach (var guid in AssetDatabase.FindAssets($"t:{nameof(AnimatorController)}", new[] { dateDir }))
                {
                    if (referencedFxGUIDs.Contains(guid))
                    {
                        referenced = true;
                        break;
                    }
                }

                if (!referenced)
                {
                    if (!AssetDatabase.DeleteAsset(dateDir))
                    {
                        throw new FacialExpressionSwitcherException($"Failed to clean assets in {dateDir}");
                    }
                }
            }
        }
    }
}
