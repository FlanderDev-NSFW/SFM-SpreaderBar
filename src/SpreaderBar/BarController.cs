using System;
using ExposureUnnoticed2.Master.Action;
using ExposureUnnoticed2.Master.Common;
using ExposureUnnoticed2.Object3D.InteractArea;
using ExposureUnnoticed2.Object3D.Player.Scripts;
using ExposureUnnoticed2.ObjectUI.InGame.RingMenu;
using ExposureUnnoticed2.Scripts.Base;
using ExposureUnnoticed2.Scripts.InGame;
using ExposureUnnoticed2.Scripts.Sound;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.InteropTypes;
using SpreaderBar.Logic;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using SfmKit.Pure;

namespace SpreaderBar
{
    /// <summary>
    /// Drives the bar each frame: ring entry, lock state, forced spread-leg walk, model placement.
    /// Game facts used here are in docs/knowledge/api-notes (player-and-movement, ui-and-input) and mods/SpreaderBar/README.md.
    /// </summary>
    public sealed class BarController : MonoBehaviour
    {
        /// <summary>RingItemType value of our entry (the game uses 1..14 for items and 10000+ for poses).</summary>
        public const int RingItemValue = 9100;

        internal static BarController Instance;
        internal static BarModel Model;
        internal static float MinHoleDistance, MaxHoleDistance; // current telescope range (model metres), refitted per width
        internal static System.Numerics.Vector3[][] TubeOriginals;
        internal static float FullLengthHoleMax;                 // model metres, tubes at full length
        public static float WidthNow;                           // cm, applied (diagnostics: game get SpreaderBar.BarController::WidthNow)

        // --- width (+ / -) ----------------------------------------------------------------------------------------------

        /// <summary>Widest width the model allows (cm), within the config's maximum.</summary>
        internal static float WidestCm() => Math.Min(Plugin.MaxWidthCm.Value, FullLengthHoleMax * Plugin.Scale.Value * 100f - 3f);

        /// <summary>Refit the tubes so <paramref name="cm"/> (clamped) lies inside the telescope; returns the width used.</summary>
        internal static float FitModel(BarModel model, float cm)
        {
            cm = Math.Clamp(cm, Plugin.MinWidthCm.Value, Math.Max(Plugin.MinWidthCm.Value, WidestCm()));
            float scale = Plugin.Scale.Value;
            (MinHoleDistance, MaxHoleDistance) = BarFit.FitFor(model, TubeOriginals, cm / 100f / scale);
            cm = Math.Clamp(cm, MinHoleDistance * scale * 100f, MaxHoleDistance * scale * 100f); // the tubes cannot get shorter
            WidthNow = cm;
            return cm;
        }

        private float _repeatAt;

        [HideFromIl2Cpp]
        private void WatchWidthKeys()
        {
            if (Math.Abs(Plugin.WidthCm.Value - WidthNow) > 1e-3f) ApplyWidth(Plugin.WidthCm.Value, false); // config edited
            if (!Machine.Worn || UiOpen()) return;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;
            // US: "+" is Shift + "=", so the "=" key counts as "+" too (de layout: "+" has its own key, "=" is not found).
            int dir = Held(kb, "+", kb.numpadPlusKey) || Held(kb, "=", null) ? 1 : Held(kb, "-", kb.numpadMinusKey) ? -1 : 0;
            if (dir == 0) { _repeatAt = 0f; return; }
            bool first = _repeatAt == 0f;
            if (!first && Time.unscaledTime < _repeatAt) return;
            _repeatAt = Time.unscaledTime + (first ? 0.4f : 0.08f); // hold to repeat
            ApplyWidth(WidthNow + dir * Plugin.StepCm.Value, true);
        }

        private static bool Held(UnityEngine.InputSystem.Keyboard kb, string character, UnityEngine.InputSystem.Controls.KeyControl numpad)
        {
            if (numpad != null && numpad.isPressed) return true;
            var key = kb.FindKeyOnCurrentKeyboardLayout(character); // the key printed "+" / "-" on this layout (de: "+" is right of Ü)
            return key != null && key.isPressed;
        }

        [HideFromIl2Cpp]
        private void ApplyWidth(float cm, bool fromKeys)
        {
            float used = FitModel(Model, cm);
            _rig?.RefreshTubes();
            if (Math.Abs(Plugin.WidthCm.Value - used) > 1e-3f) Plugin.WidthCm.Value = used; // saved to the config
            if (fromKeys) Plugin.Logger.LogInfo("[SFM-ACCEPT] width: " + used.ToString("0.0") + " cm");
        }

        internal readonly LockMachine Machine = new LockMachine();
        internal bool InteractHeld;

        private BarRig _rig;
        private IntPtr _ringArray, _gameData;
        private float _nextPoseTry;
        private bool _tunedFromGame, _releasePose;
        private Transform _uiLayer;
        private static Il2CppSystem.Func<bool> _alwaysReleased;
        private static int _failures;
        private int _refusals;
        private bool _poseRequested;

        public BarController(IntPtr pointer) : base(pointer) { }

        private void Awake()
        {
            Instance = this;
            Machine.AttemptSeconds = Plugin.AttemptSeconds.Value;
            Machine.BaseChance = Plugin.SuccessChance.Value;
        }

        internal static void Fail(string where, Exception e)
        {
            if (_failures++ < 20) Plugin.Logger.LogWarning(where + " failed: " + e);
        }

        private void Update()
        {
            try { Tick(); }
            catch (Exception e) { Fail("Update", e); }
        }

        // Placed just before rendering: in LateUpdate the legs are not final yet (the game adjusts the feet afterwards;
        // the bar sat 7 cm outside the ankles there) [observed 2026-10-11].
        private static Camera.CameraCallback _preCull;
        private int _placedFrame = -1;

        private void Start()
        {
            _preCull = DelegateSupport.ConvertDelegate<Camera.CameraCallback>(new Action<Camera>(_ => OnPreCull()));
            Camera.onPreCull = Camera.onPreCull == null
                ? _preCull
                : Il2CppSystem.Delegate.Combine(Camera.onPreCull, _preCull).Cast<Camera.CameraCallback>();
        }


        private void LateUpdate()
        {
            // Measured before the legs are changed below: the bake then matches the bones it is compared with. Only the
            // outline's size and shape are used (see FitSide).
            try { if (Machine.Worn && Plugin.FitCuffs.Value && Time.time >= _nextGirth) MeasureGirth(); }
            catch (Exception e) { Fail("measuring the legs", e); _nextGirth = Time.time + 10f; }
            // Both modes: the bar holds the ankles at its width in every pose and action.
            try { if (Machine.Worn && (Plugin.DebugDisable.Value & 256) == 0) SpreadLegs(); }
            catch (Exception e) { Fail("spreading the legs", e); }
        }

        [HideFromIl2Cpp]
        private void OnPreCull()
        {
            if (_placedFrame == Time.frameCount) return; // once per frame, before the first camera
            _placedFrame = Time.frameCount;
            try { Place(); }
            catch (Exception e) { Fail("placing the bar", e); }
        }

        [HideFromIl2Cpp]
        private void Tick()
        {
            EnsureRingEntry();
            WatchRingChoice();
            SyncShortcuts();
            WatchShortcuts();
            WatchWidthKeys();
            TrackSaveIdentity();
            var player = PlayerController.Instance;
            if (player == null || GameState.GameStateData == null) return;
            if (!_tunedFromGame) TuneFromGame();

            Machine.AttemptSeconds = Plugin.AttemptSeconds.Value; // live: config edits apply without a restart
            Machine.BaseChance = Plugin.SuccessChance.Value;
            // Interactions take precedence: no unlocking inside an interaction area (closet, PC, machines ...), even though the
            // pose hides their prompt; nor while the game's own handcuffs use F, nor in menus.
            bool unlockAllowed = InteractHeld && Machine.State == LockState.Locked && !GameStateFacade.IsAttachSomeHandcuffs()
                                 && !UiOpen() && !InInteractArea();
            var ev = Machine.Tick(Time.deltaTime, unlockAllowed, UnityEngine.Random.value);
            Status.Update(Machine, InteractHeld, unlockAllowed);
            switch (ev)
            {
                case LockMachine.Event.Locked:
                    PlaySe(InGameSoundManager.SeType.AttachHandcuffs);
                    Plugin.Logger.LogInfo("[SFM-ACCEPT] lock: spreader bar locked");
                    break;
                case LockMachine.Event.AttemptFailed:
                    Plugin.Logger.LogInfo("[SFM-ACCEPT] attempt: failed, chance now " + Machine.Chance.ToString("0.00"));
                    break;
                case LockMachine.Event.Unlocking:
                    PlaySe(InGameSoundManager.SeType.Unlock);
                    Plugin.Logger.LogInfo("[SFM-ACCEPT] attempt: success after " + Machine.Failures + " failures");
                    break;
                case LockMachine.Event.Unlocked:
                    _releasePose = Plugin.Mode.Value == BarMode.SpreadLegWalk;
                    Plugin.Logger.LogInfo("[SFM-ACCEPT] unlock: spreader bar removed");
                    break;
            }

            var pca = player.Pca;
            bool poseMode = Plugin.Mode.Value == BarMode.SpreadLegWalk;
            if (Machine.Worn && poseMode) KeepPose(pca);
            else if (_releasePose) ReleasePose(pca);
        }

        // --- ring choice --------------------------------------------------------------------------------------------------
        // A click on the selected ring entry runs it. There is no hook on the game's handler (RingMenuParentView.
        // OnInvokeCommandCommon: patching it made saving crash) and the ring views are rebuilt on every opening, so the
        // choice is read here: our entry selected on the shown page + a left click this frame [observed 2026-10-11].

        [HideFromIl2Cpp]
        private void WatchRingChoice()
        {
            var parent = RingMenuParentView.Instance;
            if (parent == null || !parent.IsOpenRing) return;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;
            var views = parent.openRingList;
            int page = parent.CurrentRingIndexCommon;
            if (views == null || page < 0 || page >= views.Count) return;
            var selected = views[page] == null ? null : views[page].currentSelectView;
            if (selected != null && selected.Model != null && (int)selected.Model.RingItemType == RingItemValue) OnRingChosen();
        }

        // Number shortcuts: releasing ring key N over an entry stores it in that key's lastInvokeModel* (shown bottom right)
        // and its index in GameStateData.RingShortcutModelIndex*; a short tap of N runs it through OnInvokeCommandCommon,
        // which ignores our entry. The index is saved, and on load RingMenuParentView.Initialize reads
        // commonRingMenuModel[index] before our entry exists: index 31 threw IndexOutOfRange and the stage never started
        // [observed 2026-10-11]. So the stored index never points at our entry (the key's previous vanilla entry is kept
        // there), the keys holding the bar are saved under our own key and put back whenever the ring is rebuilt.

        /// <summary>Common ring entries without the mod (used to repair saves written by 0.1.0) [observed 2026-10-11].</summary>
        internal static int VanillaRingCount = 31;
        private int _ringIndex = -1;           // our entry's index in the current ring array
        private int _barKeys;                  // bit k: shortcut key k+1 runs the bar
        private bool _applyShortcuts;          // a new ring array (load, stage change): put the bar back on its keys
        private readonly int[] _fallbackIndex = new int[8];

        internal int ShortcutMask => _barKeys;

        internal void OnShortcutsLoaded(int mask)
        {
            _barKeys = mask & 0xFF;
            _applyShortcuts = true;
        }

        [HideFromIl2Cpp]
        private void SyncShortcuts()
        {
            var parent = RingMenuParentView.Instance;
            var data = GameState.GameStateData;
            if (parent == null || data == null || _ringIndex < 0 || _ringArray != parent.commonRingMenuModel?.Pointer) return;
            var ours = parent.commonRingMenuModel[_ringIndex];
            if (_applyShortcuts)
            {
                _applyShortcuts = false;
                for (int k = 0; k < 8; k++)
                    if ((_barKeys & (1 << k)) != 0) SetShortcutModel(parent, k, ours);
                if (_barKeys != 0) Plugin.Logger.LogInfo("[SFM-ACCEPT] shortcut: bar restored on keys " + KeyList(_barKeys));
            }
            for (int k = 0; k < 8; k++)
            {
                var model = ShortcutModel(parent, k);
                int index = ShortcutIndex(data, k);
                if (model != null && (int)model.RingItemType == RingItemValue)
                {
                    if ((_barKeys & (1 << k)) == 0) Plugin.Logger.LogInfo("[SFM-ACCEPT] shortcut: bar assigned to key " + (k + 1));
                    _barKeys |= 1 << k;
                    if (index == _ringIndex) SetShortcutIndex(data, k, _fallbackIndex[k]);
                }
                else
                {
                    _barKeys &= ~(1 << k); // reassigned to a vanilla entry
                    if (index >= 0 && index < VanillaRingCount) _fallbackIndex[k] = index;
                }
            }
        }

        private static string KeyList(int mask)
        {
            var s = "";
            for (int k = 0; k < 8; k++) if ((mask & (1 << k)) != 0) s += (s.Length == 0 ? "" : ",") + (k + 1);
            return s;
        }

        [HideFromIl2Cpp]
        private static void SetShortcutModel(RingMenuParentView p, int key, RingMenuItemModel model)
        {
            switch (key)
            {
                case 0: p.lastInvokeModelCommon = model; break;
                case 1: p.lastInvokeModelSkill = model; break;
                case 2: p.lastInvokeModelSkill3 = model; break;
                case 3: p.lastInvokeModelSkill4 = model; break;
                case 4: p.lastInvokeModelSkill5 = model; break;
                case 5: p.lastInvokeModelSkill6 = model; break;
                case 6: p.lastInvokeModelSkill7 = model; break;
                default: p.lastInvokeModelSkill8 = model; break;
            }
            var views = p.shortcutItemViews;
            if (views == null) return;
            foreach (var v in views)
                if (v != null && (int)v.type == key) v.SetName(model);
        }

        internal static int ShortcutIndex(GameStateData d, int key) => key switch
        {
            0 => d.RingShortcutModelIndexCommon, 1 => d.RingShortcutModelIndexSkill, 2 => d.RingShortcutModelIndexSkill3,
            3 => d.RingShortcutModelIndexSkill4, 4 => d.RingShortcutModelIndexSkill5, 5 => d.RingShortcutModelIndexSkill6,
            6 => d.RingShortcutModelIndexSkill7, _ => d.RingShortcutModelIndexSkill8,
        };

        internal static void SetShortcutIndex(GameStateData d, int key, int index)
        {
            switch (key)
            {
                case 0: d.RingShortcutModelIndexCommon = index; break;
                case 1: d.RingShortcutModelIndexSkill = index; break;
                case 2: d.RingShortcutModelIndexSkill3 = index; break;
                case 3: d.RingShortcutModelIndexSkill4 = index; break;
                case 4: d.RingShortcutModelIndexSkill5 = index; break;
                case 5: d.RingShortcutModelIndexSkill6 = index; break;
                case 6: d.RingShortcutModelIndexSkill7 = index; break;
                default: d.RingShortcutModelIndexSkill8 = index; break;
            }
        }

        private static readonly InputManager.InputType[] ShortcutKeys =
        {
            InputManager.InputType.CommonRingMenu, InputManager.InputType.SkillRingMenu, InputManager.InputType.SkillRingMenu3,
            InputManager.InputType.SkillRingMenu4, InputManager.InputType.SkillRingMenu5, InputManager.InputType.SkillRingMenu6,
            InputManager.InputType.SkillRingMenu7, InputManager.InputType.SkillRingMenu8,
        };

        [HideFromIl2Cpp]
        private void WatchShortcuts()
        {
            var parent = RingMenuParentView.Instance;
            if (parent == null || parent.IsOpenRing || PlayerController.Instance == null || UiOpen()) return;
            for (int i = 0; i < ShortcutKeys.Length; i++)
            {
                var model = ShortcutModel(parent, i);
                if (model == null || (int)model.RingItemType != RingItemValue) continue;
                if (!parent.GetShortUp(ShortcutKeys[i])) continue;
                Plugin.Logger.LogInfo("[SFM-ACCEPT] shortcut: key " + (i + 1) + " runs the spreader bar entry");
                OnRingChosen();
                return;
            }
        }

        private static RingMenuItemModel ShortcutModel(RingMenuParentView p, int key) => key switch
        {
            0 => p.lastInvokeModelCommon, 1 => p.lastInvokeModelSkill, 2 => p.lastInvokeModelSkill3, 3 => p.lastInvokeModelSkill4,
            4 => p.lastInvokeModelSkill5, 5 => p.lastInvokeModelSkill6, 6 => p.lastInvokeModelSkill7, _ => p.lastInvokeModelSkill8,
        };

        /// <summary>Our ring entry was chosen.</summary>
        [HideFromIl2Cpp]
        internal void OnRingChosen()
        {
            if (Machine.RequestLock())
            {
                _nextGirth = 0f;
                _girth[0].Ok = _girth[1].Ok = false; // measure the legs afresh
                _girth[0].Shown = _girth[1].Shown = NVector3.Zero;
                PlaySe(InGameSoundManager.SeType.CommonEquip);
                _nextPoseTry = Time.time + 0.3f; // the ring is still closing: an immediate pose change is refused
                Plugin.Logger.LogInfo("[SFM-ACCEPT] ring: spreader bar chosen, closing");
            }
            else Plugin.Logger.LogInfo("ring: spreader bar already worn (hold the interact key to try to unlock)");
        }

        [HideFromIl2Cpp]
        internal void OnSaveLoaded(bool locked, float chance)
        {
            Machine.Restore(locked, chance);
            _gameData = GameState.GameStateData == null ? IntPtr.Zero : GameState.GameStateData.Pointer;
            _nextPoseTry = Time.time + 1f; // the stage is still loading; the pose is refused until the player is live
        }

        /// <summary>A new game or the title screen replaces the game data without a load: drop the bar.</summary>
        [HideFromIl2Cpp]
        private void TrackSaveIdentity()
        {
            var data = GameState.GameStateData;
            IntPtr ptr = data == null ? IntPtr.Zero : data.Pointer;
            if (ptr == _gameData) return;
            _gameData = ptr;
            if (Machine.Worn) Plugin.Logger.LogInfo("game data replaced without a load: spreader bar reset");
            Machine.Reset();
            _barKeys = 0;
        }

        [HideFromIl2Cpp]
        private void TuneFromGame()
        {
            _tunedFromGame = true;
            try
            {
                var common = MCommon.Instance;
                if (common == null) return;
                if (common.HandcuffsAddProbabilityByFailure > 0f) Machine.ChancePerFailure = common.HandcuffsAddProbabilityByFailure;
            }
            catch (Exception e) { Fail("reading handcuff tuning", e); }
        }

        // --- pose --------------------------------------------------------------------------------------------------------

        // The bar does not block anything itself; the vanilla spread-leg walk refuses crouching, the handcuffs and other actions
        // ("Can't do that now") [observed 2026-10-11]. So when the player asks for one, the pose is ended right in the input
        // hook (before the game handles the input this frame), the action / crouch / menu plays out, and the pose comes back
        // once the player is idle again.
        private const float ReturnDelay = 0.4f;
        private float _idleSince = -1f;
        private int _yieldLogs;

        /// <summary>Input of this frame, after the game resolved it (bar worn).</summary>
        [HideFromIl2Cpp]
        internal void OnInput(InputData r)
        {
            var pca = PlayerController.Instance == null ? null : PlayerController.Instance.Pca;
            var action = pca == null || pca.PlayerState == null ? null : pca.PlayerState.CurrentAction;
            if (action == null) return;
            bool inPose = action.Type == ActionType.GanimataWalk;
            // Walking speed: no running while the bar is on - except in another pose, which Shift + move ends (as in vanilla).
            if (inPose || !MAction.IsPoseSkill(action.Type))
            {
                r.Dash = false;
                r.JustAutoRun = false;
            }
            if (!inPose || Plugin.Mode.Value != BarMode.SpreadLegWalk) return; // LegsApart: the player's own poses stay vanilla
            string why = r.JustCrouch ? "crouch"
                : r.JustRingHandcuff ? "handcuffs"
                : r.JustPee ? "pee"
                : r.JustDrinkWater ? "drink"
                : r.JustDrop ? "drop"
                : r.JustBra || r.JustPants ? "underwear"
                : r.JustOnaniNormal ? "masturbate"
                : r.JustRingDildo ? "dildo"
                : r.JustRingEyeMask || r.JustEyeMask ? "eye mask"
                : r.JustDropHandcuffKey || r.JustDropVibeRemocon ? "drop item"
                : r.JustSwitchVibrator || r.JustSwitchPistonMachine || r.JustTimeStop ? "switch"
                // The handcuff struggle (hold F) is not allowed in the pose (CanUnlock false); with the bar and the cuffs on,
                // F goes to the cuffs first.
                : r.Interact && GameStateFacade.IsAttachSomeHandcuffs() && !InInteractArea() ? "handcuff struggle"
                : null;
            if (why == null || pca.PlayerAction == null) return;
            pca.PlayerAction.ChangeAction(ActionType.None);
            _idleSince = -1f;
            if (_yieldLogs++ < 30) Plugin.Logger.LogInfo("[SFM-ACCEPT] pose: yielding to " + why);
        }

        [HideFromIl2Cpp]
        private void KeepPose(PlayerClassAccessor pca)
        {
            var action = pca.PlayerState.CurrentAction;
            if (action == null) return;
            if (action.Type == ActionType.GanimataWalk) { _poseRequested = false; _idleSince = -1f; return; }
            // Other actions and poses (pee, items, the handcuff struggle, events) play out; the pose returns afterwards.
            bool cuffed = GameStateFacade.IsAttachSomeHandcuffs();
            if (action.Type != ActionType.None || UiOpen() || RingOpen() || pca.PlayerState.IsCrouch || (cuffed && InteractHeld))
            {
                _idleSince = -1f;
                return;
            }
            if (_idleSince < 0f) _idleSince = Time.time;
            if (Time.time - _idleSince < ReturnDelay || Time.time < _nextPoseTry) return;
            var facade = PlayerFacade.Instance;
            if (facade == null || pca.PlayerAction == null) return;
            // The change shows on a later frame, so a refusal is only known at the next try. Refused tries make the game
            // show a message each time: back off.
            if (_poseRequested)
            {
                _nextPoseTry = Time.time + 3f;
                _poseRequested = false;
                if (_refusals++ < 5) Plugin.Logger.LogInfo("pose: the game refused the spread-leg walk (action " + action.Type + ")");
                return;
            }
            // With cuffed arms TransAction refuses the pose ("arms are restrained"); ChangeAction still puts the legs in it
            // and the cuffed arms stay behind the back [observed 2026-10-11].
            if (cuffed) pca.PlayerAction.ChangeAction(ActionType.GanimataWalk);
            else facade.TransAction(ActionType.GanimataWalk);
            _poseRequested = true;
            _nextPoseTry = Time.time + 0.5f;
        }

        [HideFromIl2Cpp]
        private void ReleasePose(PlayerClassAccessor pca)
        {
            _releasePose = false;
            var action = pca.PlayerState.CurrentAction;
            // PlayerFacade.TransAction(None) is ignored for poses; PlayerAction.ChangeAction(None) ends it [observed 2026-10-11].
            if (action != null && action.Type == ActionType.GanimataWalk && pca.PlayerAction != null)
                pca.PlayerAction.ChangeAction(ActionType.None);
        }

        // --- model -------------------------------------------------------------------------------------------------------

        [HideFromIl2Cpp]
        private void Place()
        {
            var player = PlayerController.Instance;
            bool show = Machine.Worn && player != null && GameState.GameStateData != null;
            if (!show)
            {
                _rig?.SetVisible(false);
                return;
            }
            var anim = player.Pca.Animator;
            if (anim == null) return;
            Transform footR = anim.GetBoneTransform(HumanBodyBones.RightFoot), footL = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform kneeR = anim.GetBoneTransform(HumanBodyBones.RightLowerLeg), kneeL = anim.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            if (footR == null || footL == null || kneeR == null || kneeL == null) return;

            if (_rig == null) _rig = new BarRig(Model, Plugin.Scale.Value);
            if (!_rig.Built) _rig.Build(footR.gameObject.layer);

            NVector3 fr = N(footR.position), fl = N(footL.position);
            NVector3 shinR = N(kneeR.position) - fr, shinL = N(kneeL.position) - fl;
            float lift = Plugin.CuffHeight.Value;
            NVector3 ankleR = fr + BarPose.Unit(shinR, NVector3.UnitY) * lift, ankleL = fl + BarPose.Unit(shinL, NVector3.UnitY) * lift;
            float scale = Plugin.Scale.Value;
            NVector3 cuffR = new NVector3(scale), cuffL = new NVector3(scale);
            if (Plugin.FitCuffs.Value)
            {
                // Each cuff scaled per axis to the leg's outline (an oval leg gets an oval cuff), centred on the shin bone;
                // the outline is measured every 2 s, the scale eased so a new measurement does not pop.
                float ease = 1f - (float)Math.Exp(-Time.deltaTime * 6f);
                NVector3 across = ankleL - ankleR; // the cuffs' x points along the bar
                FitSide(0, kneeR, shinR, across, ankleR, ref cuffR, ease);
                FitSide(1, kneeL, shinL, across, ankleL, ref cuffL, ease);
            }
            CuffScaleR = cuffR.X;
            CuffScaleL = cuffL.X;
            var input = new BarPose.Input
            {
                AnkleR = ankleR,
                AnkleL = ankleL,
                ShinR = shinR, ShinL = shinL,
                Up = N(player.transform.up), Forward = N(player.transform.forward),
            };
            var pose = BarPose.Solve(input, Model.Sides[0], Model.Sides[1], cuffR, cuffL, scale, MinHoleDistance, MaxHoleDistance);
            _rig.SetCuffScales(cuffR, cuffL);
            _rig.Apply(pose, Machine.Closed);
            _rig.SetVisible(true);
        }

        private static NVector3 N(Vector3 v) => new NVector3(v.x, v.y, v.z);
        private static Vector3 V(NVector3 v) => new Vector3(v.X, v.Y, v.Z);

        // --- cuff fit: size each cuff to the leg (body + socks + shoes) ------------------------------------------------
        // Every 2 s the player's skinned meshes near the ankles are baked (CPU skinning of the current pose); the vertices in
        // a thin slice at cuff height give the leg's cross-section (Logic/LegGirth.cs). Kept relative to the shin bone, so the
        // cuffs follow the leg between measurements.
        private static readonly float[] SliceHeights = { -0.01f, 0f, 0.01f };
        private struct Girth
        {
            public bool Ok;
            public float Radius;
            public NVector3[] LocalOutline; // the leg's outline at cuff height around its own centre, in the shin's rotation
            public NVector3 Shown;          // eased cuff scale
        }
        private readonly Girth[] _girth = new Girth[2];
        private float _nextGirth;
        private int _girthLogs;
        private string _girthMeshes = "";

        /// <summary>"name R12/L8; ..." -> "name;name;" (which meshes were cut, not how many points).</summary>
        private static string MeshNames(string sources)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var part in sources.Split(';'))
            {
                string t = part.Trim();
                int sp = t.LastIndexOf(' ');
                if (sp > 0) sb.Append(t, 0, sp).Append(';');
            }
            return sb.ToString();
        }
        private float _loggedGirth;
        public static float GirthR, GirthL, CuffScaleR, CuffScaleL; // diagnostics (leg radius m, cuff scale)
        public static int GirthPointsR, GirthPointsL;
        public static string GirthSources = "", FitR = "", FitL = ""; // diagnostics: meshes cut; fitted size per side

        private readonly System.Collections.Generic.List<NVector3> _outlineWorld = new System.Collections.Generic.List<NVector3>();

        [HideFromIl2Cpp]
        private void FitSide(int side, Transform shin, NVector3 shinAxis, NVector3 across, NVector3 ankle, ref NVector3 cuffScale, float ease)
        {
            ref Girth g = ref _girth[side];
            if (!g.Ok || g.LocalOutline == null) return;
            var rot = new System.Numerics.Quaternion(shin.rotation.x, shin.rotation.y, shin.rotation.z, shin.rotation.w);
            _outlineWorld.Clear();
            foreach (var p in g.LocalOutline) _outlineWorld.Add(ankle + NVector3.Transform(p, rot));
            var (_, hx, hz) = LegGirth.Extents(_outlineWorld, ankle, shinAxis, across);
            NVector3 want = LegGirth.OvalScale(hx, hz, Plugin.ClearanceCm.Value / 100f, Model.Sides[side].HoleRadius, 0.3f, 2.5f,
                                               Plugin.MaxOval.Value);
            g.Shown = g.Shown == NVector3.Zero ? want : NVector3.Lerp(g.Shown, want, ease);
            cuffScale = g.Shown;
            string fit = "half " + (hx * 100f).ToString("0.0") + " x " + (hz * 100f).ToString("0.0") + " cm, scale " + g.Shown.X.ToString("0.00") + " / " + g.Shown.Z.ToString("0.00");
            if (side == 0) FitR = fit; else FitL = fit;
        }

        /// <summary>Unity's column-vector matrix as a System.Numerics (row-vector) matrix.</summary>
        private static System.Numerics.Matrix4x4 ToNumerics(Matrix4x4 u) => new System.Numerics.Matrix4x4(
            u.m00, u.m10, u.m20, u.m30,
            u.m01, u.m11, u.m21, u.m31,
            u.m02, u.m12, u.m22, u.m32,
            u.m03, u.m13, u.m23, u.m33);

        // The leg's outline: the player's skinned meshes near the ankles are baked (the game's meshes are not CPU-readable,
        // so their bind pose cannot be used) and cut with the cuff plane. Experiments [observed 2026-10-12]: baked in
        // LateUpdate before the leg changes with BakeMesh(mesh, true) and pos + rot * v the outline is a clean ring of the
        // right size (without useScale it came out ~30 % too big; baked in Camera.onPreCull it did not match the bones at
        // all). Its position is not reliable though - the game moves the legs again after LateUpdate and the skin near the
        // ankle partly follows the foot (one outline sat 4 cm off its leg) - so only its size and shape are kept, and the
        // cuff stays centred on the shin bone, which matches the drawn leg.
        private Mesh _bakeMesh;

        [HideFromIl2Cpp]
        private void MeasureGirth()
        {
            _nextGirth = Time.time + 2f;
            var player = PlayerController.Instance;
            var anim = player == null ? null : player.Pca.Animator;
            if (anim == null) return;
            Transform footR = anim.GetBoneTransform(HumanBodyBones.RightFoot), footL = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform kneeR = anim.GetBoneTransform(HumanBodyBones.RightLowerLeg), kneeL = anim.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            if (footR == null || footL == null || kneeR == null || kneeL == null) return;
            float lift = Plugin.CuffHeight.Value;
            Vector3 axisR = kneeR.position - footR.position, axisL = kneeL.position - footL.position;
            Vector3 cR = footR.position + axisR.normalized * lift, cL = footL.position + axisL.normalized * lift;
            const float near = 0.15f;
            var ptsR = new System.Collections.Generic.List<NVector3>();
            var ptsL = new System.Collections.Generic.List<NVector3>();
            if (_bakeMesh == null) _bakeMesh = new Mesh { name = "SpreaderBar.GirthBake" };
            string sources = "";
            foreach (var r in anim.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (r == null || !r.enabled || r.sharedMesh == null) continue;
                var bnd = r.bounds;
                if (bnd.SqrDistance(cR) > near * near && bnd.SqrDistance(cL) > near * near) continue;
                r.BakeMesh(_bakeMesh, true);
                var verts = _bakeMesh.vertices;
                Transform t = r.transform;
                Vector3 pos = t.position;
                Quaternion rot = t.rotation;
                var world = new NVector3[verts.Length];
                for (int i = 0; i < verts.Length; i++) world[i] = N(pos + rot * verts[i]);
                var triArray = _bakeMesh.triangles;
                var tris = new int[triArray.Length];
                for (int i = 0; i < tris.Length; i++) tris[i] = triArray[i];
                int beforeR = ptsR.Count, beforeL = ptsL.Count;
                // Three cuts over the ring's height (the leg narrows towards the ankle); the widest decides.
                foreach (float h in SliceHeights)
                {
                    LegGirth.Slice(world, tris, N(cR + axisR.normalized * h), N(axisR), near, ptsR);
                    LegGirth.Slice(world, tris, N(cL + axisL.normalized * h), N(axisL), near, ptsL);
                }
                if (ptsR.Count > beforeR || ptsL.Count > beforeL)
                    sources += r.name + " R" + (ptsR.Count - beforeR) + "/L" + (ptsL.Count - beforeL) + "; ";
            }
            float maxR = Math.Min(0.09f, WidthNow / 100f * 0.45f); // never reach the other leg
            string meshes = MeshNames(sources);
            if (meshes != _girthMeshes) { _girthMeshes = meshes; _girth[0].Ok = _girth[1].Ok = false; } // other clothes: measure afresh
            Store(0, LegGirth.Measure(ptsR, N(cR), N(axisR), 0.012f, maxR), kneeR);
            Store(1, LegGirth.Measure(ptsL, N(cL), N(axisL), 0.012f, maxR), kneeL);
            GirthSources = sources;
            GirthR = _girth[0].Radius;
            GirthL = _girth[1].Radius;
            if (_girthLogs++ < 3 || Math.Abs(GirthR - _loggedGirth) > 0.003f)
            {
                _loggedGirth = GirthR;
                Plugin.Logger.LogInfo("[SFM-ACCEPT] cuff fit: leg radius R " + (GirthR * 100f).ToString("0.0") + " cm (" + GirthPointsR + " pts), L " +
                                      (GirthL * 100f).ToString("0.0") + " cm (" + GirthPointsL + " pts); " + GirthSources);
            }
        }

        [HideFromIl2Cpp]
        private void Store(int side, LegGirth.Fit fit, Transform shin)
        {
            if (side == 0) GirthPointsR = fit.Points; else GirthPointsL = fit.Points;
            if (!fit.Ok) return; // keep the last good fit (or the plain Scale)
            // A leg does not get thinner with the pose, but the measurement can (crawling read 2.1 cm instead of 3.0 cm,
            // the bake cutting nearer the ankle) [observed 2026-10-12]: keep the largest outline since the bar was put on
            // or the clothes at the ankle changed.
            if (_girth[side].Ok && fit.Radius <= _girth[side].Radius) return;
            // Offsets from the outline's own centre, turned into the shin's frame (rotation only).
            var toShin = System.Numerics.Quaternion.Inverse(new System.Numerics.Quaternion(shin.rotation.x, shin.rotation.y, shin.rotation.z, shin.rotation.w));
            var local = new NVector3[fit.Outline.Count];
            for (int i = 0; i < local.Length; i++) local[i] = NVector3.Transform(fit.Outline[i] - fit.Center, toShin);
            _girth[side].Ok = true;
            _girth[side].Radius = fit.Radius;
            _girth[side].LocalOutline = local;
        }

        private static Quaternion U(System.Numerics.Quaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);

        // LegsApart: after all animation and the game's foot adjustments (this runs just before rendering, like the bar
        // placement), hold the cuffs at the bar's length: both ankles move along the line between them, each leg follows
        // with a two-bone solve, the feet keep their orientation. The animator rewrites the bones every frame, so nothing
        // accumulates. Pure maths: Logic/LegSpread.cs.
        public static float DebugExtraSpread; // metres added to the bar's range (bridge: game set), to make the effect obvious in tests
        public static float LastSpreadBefore, LastSpreadAfter, LastHipDrop; // diagnostics, readable with the bridge (cuff distance before / after)

        [HideFromIl2Cpp]
        private void SpreadLegs()
        {
            var player = PlayerController.Instance;
            var anim = player == null ? null : player.Pca.Animator;
            if (anim == null || Model == null) return;
            Transform hipR = anim.GetBoneTransform(HumanBodyBones.RightUpperLeg), hipL = anim.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            Transform kneeR = anim.GetBoneTransform(HumanBodyBones.RightLowerLeg), kneeL = anim.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            Transform footR = anim.GetBoneTransform(HumanBodyBones.RightFoot), footL = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
            if (hipR == null || hipL == null || kneeR == null || kneeL == null || footR == null || footL == null) return;

            Transform hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            float lift = Plugin.CuffHeight.Value;
            var right = new Leg { Hip = N(hipR.position), Knee = N(kneeR.position), Ankle = N(footR.position), CuffLift = lift };
            var left = new Leg { Hip = N(hipL.position), Knee = N(kneeL.position), Ankle = N(footL.position), CuffLift = lift };
            NVector3 up = N(player.transform.up);
            // the right leg is on the body's right: right -> left is -right
            var r = LegSpread.Solve(right, left, WidthNow / 100f + DebugExtraSpread, -N(player.transform.right), up,
                Plugin.StrideAngle.Value * Mathf.Deg2Rad);
            LastSpreadBefore = r.Before;
            LastSpreadAfter = r.After;
            LastHipDrop = r.HipDrop;
            if (r.HipDrop > 0f && hips != null) hips.position -= player.transform.up * r.HipDrop; // legs out of reach: bend the knees
            ApplyLeg(hipR, kneeR, footR, r.Right);
            ApplyLeg(hipL, kneeL, footL, r.Left);
        }

        private static void ApplyLeg(Transform thigh, Transform shin, Transform foot, LegFix fix)
        {
            if (!fix.Changed) return;
            Quaternion shinOld = shin.rotation, footOld = foot.rotation;
            Quaternion t = U(fix.Thigh);
            thigh.rotation = t * thigh.rotation;
            shin.rotation = t * U(fix.Knee) * shinOld;
            foot.rotation = footOld;
        }

        // --- ring entry --------------------------------------------------------------------------------------------------

        [HideFromIl2Cpp]
        private void EnsureRingEntry()
        {
            var view = RingMenuParentView.Instance;
            if (view == null) return;
            var models = view.commonRingMenuModel;
            if (models == null || models.Pointer == _ringArray) return;
            for (int i = 0; i < models.Length; i++)
                if (models[i] != null && (int)models[i].RingItemType == RingItemValue)
                {
                    _ringArray = models.Pointer;
                    _ringIndex = i;
                    _applyShortcuts = true;
                    return;
                }

            if (_alwaysReleased == null) _alwaysReleased = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<bool>>(new Func<bool>(() => true));
            var entry = new RingMenuItemModel
            {
                RingItemType = (RingMenuParentView.RingItemType)RingItemValue,
                NameKey = Patches.LabelKey,
                Index = models.Length,
                ReleaseCondition = _alwaysReleased,
            };
            var grown = new Il2CppReferenceArray<RingMenuItemModel>(models.Length + 1);
            for (int i = 0; i < models.Length; i++) grown[i] = models[i];
            grown[models.Length] = entry;
            view.commonRingMenuModel = grown;
            _ringArray = grown.Pointer;
            _ringIndex = models.Length;
            // Only the game's own entries count (other mods - e.g. WallRestraint - may have added theirs first, values 9000+):
            // a stored shortcut index must stay below this, or loading crashes.
            VanillaRingCount = SfmKit.Game.RingEntry.CountVanilla(models);
            _applyShortcuts = true;
            Plugin.Logger.LogInfo("[SFM-ACCEPT] ring: entry added at index " + models.Length);
            // 8 slots per page; the ring adds pages as needed (33 entries -> 5 pages) [observed 2026-10-24, with WallRestraint].
        }

        // --- helpers -----------------------------------------------------------------------------------------------------

        private Il2CppArrayBase<InteractAreaController> _areas;
        private float _areasTime = -10f;

        /// <summary>The player stands in an interaction area. InteractManager.CurrentInteractModel stayed null although the
        /// closet prompt was shown, so the areas' own flag is used [observed 2026-10-11].</summary>
        [HideFromIl2Cpp]
        private bool InInteractArea()
        {
            if (_areas == null || Time.time - _areasTime > 2f) // areas come and go with stages and NPCs
            {
                _areas = UnityEngine.Object.FindObjectsOfType<InteractAreaController>();
                _areasTime = Time.time;
            }
            foreach (var area in _areas)
                if (area != null && area.isInArea && area.gameObject.activeInHierarchy) return true;
            return false;
        }

        [HideFromIl2Cpp]
        private bool UiOpen()
        {
            if (_uiLayer == null)
            {
                var go = GameObject.Find("/InGameManager/InGameCanvas/MiddleCloneLayer");
                if (go == null) return false;
                _uiLayer = go.transform;
            }
            for (int i = 0; i < _uiLayer.childCount; i++)
                if (_uiLayer.GetChild(i).gameObject.activeSelf) return true;
            return false;
        }

        private static bool RingOpen()
        {
            var view = RingMenuParentView.Instance;
            return view != null && view.IsOpenRing;
        }

        private static void PlaySe(InGameSoundManager.SeType type)
        {
            try
            {
                var facade = PlayerFacade.Instance;
                if (facade != null) facade.PlaySe(type);
            }
            catch (Exception e) { Fail("sound", e); }
        }

        private void OnDestroy()
        {
            if (_preCull != null && Camera.onPreCull != null)
                Camera.onPreCull = Il2CppSystem.Delegate.Remove(Camera.onPreCull, _preCull)?.Cast<Camera.CameraCallback>();
            _rig?.Destroy();
            if (_bakeMesh != null) Destroy(_bakeMesh);
            if (Instance == this) Instance = null;
        }
    }
}
