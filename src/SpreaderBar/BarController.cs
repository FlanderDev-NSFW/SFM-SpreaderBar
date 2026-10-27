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
    /// Drives the bar each frame: lock state, forced spread-leg walk, model placement. The ring entry, number shortcuts,
    /// F / + / - routing and the unlock gauge are RestraintHub's (Plugin.Entry).
    /// Game facts used here are in docs/knowledge/api-notes (player-and-movement, ui-and-input) and docs/mods/SpreaderBar.md.
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
        internal static float ShortestHoleMin;                   // model metres, tubes at their shortest
        public static float WidthNow;                           // cm, applied (diagnostics: game get SpreaderBar.BarController::WidthNow)

        // --- width (+ / -) ----------------------------------------------------------------------------------------------

        /// <summary>Widest width the model allows (cm), within the config's maximum.</summary>
        internal static float WidestCm() => Math.Min(Plugin.MaxWidthCm.Value, FullLengthHoleMax * Plugin.Scale.Value * 100f - 3f);

        /// <summary>Narrowest width the model allows (cm), within the config's minimum.</summary>
        internal static float NarrowestCm() => Math.Max(Plugin.MinWidthCm.Value, ShortestHoleMin * Plugin.Scale.Value * 100f);

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

        private float _configWidth = float.NaN; // the config value last applied (the bar itself may be clamped wider)

        [HideFromIl2Cpp]
        private void WatchWidthKeys()
        {
            if (Plugin.WidthCm.Value != _configWidth) // config edited (or first frame)
            {
                _configWidth = Plugin.WidthCm.Value;
                ApplyWidth(_configWidth, false);
            }
            // + / - (the printed keys, numpad, "=" on US layouts; hold repeats) reach the bar only while it is the selected
            // restraint - the hub routes them, outside menus.
            int dir = Plugin.Entry.TakeStep();
            if (dir == 0 || !Machine.Worn) return;
            ApplyWidth(WidthNow + dir * Plugin.StepCm.Value, true);
        }

        [HideFromIl2Cpp]
        private void ApplyWidth(float cm, bool fromKeys)
        {
            float used = FitModel(Model, cm);
            _rig?.RefreshTubes();
            if (!fromKeys) return;
            // Only + / - store the width. Writing back a clamped config value moved the user's setting on every launch (narrow
            // widths are clamped up to what the refitted tubes allow: 21.36 -> 21.46 -> 21.53 cm) [observed 2026-10-24].
            _configWidth = used;
            if (Math.Abs(Plugin.WidthCm.Value - used) > 1e-3f) Plugin.WidthCm.Value = used; // saved to the config
            Plugin.Logger.LogInfo("[SFM-ACCEPT] width: " + used.ToString("0.0") + " cm");
        }

        internal readonly LockMachine Machine = new LockMachine();
        internal bool InteractHeld; // F as the game sees it this frame (after the hub; OnInput)

        private BarRig _rig;
        private float _nextPoseTry;
        private bool _tunedFromGame, _releasePose;
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
            WatchWidthKeys();
            var player = PlayerController.Instance;
            if (player == null || GameState.GameStateData == null) return;
            if (!_tunedFromGame) TuneFromGame();

            Machine.AttemptSeconds = Plugin.AttemptSeconds.Value; // live: config edits apply without a restart
            Machine.BaseChance = Plugin.SuccessChance.Value;
            // F counts only while the bar is the selected restraint (with the game's handcuffs on too, whichever is selected
            // gets F), outside menus and interaction areas (closet, PC ... - the pose hides their prompt): the hub decides.
            bool unlockAllowed = Plugin.Entry.StruggleHeld && Machine.State == LockState.Locked;
            var ev = Machine.Tick(Time.deltaTime, unlockAllowed, UnityEngine.Random.value);
            Status.Update(Machine, Plugin.Entry.StruggleHeld, unlockAllowed);
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

            try { UpdateSpeed(); }
            catch (Exception e) { Fail("walking speed", e); }

            var pca = player.Pca;
            bool poseMode = Plugin.Mode.Value == BarMode.SpreadLegWalk;
            if (Machine.Worn && poseMode) KeepPose(pca);
            else if (_releasePose) ReleasePose(pca);
        }

        /// <summary>Common ring entries of the game (a stored shortcut index must stay below; used to repair saves written by
        /// 0.1.0) [observed 2026-10-11].</summary>
        internal const int VanillaRingCount = 31;

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

        /// <summary>The ring entry or a number key, when the bar is off and nothing worn holds her legs (the hub checked).</summary>
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
            _nextPoseTry = Time.time + 1f; // the stage is still loading; the pose is refused until the player is live
        }

        /// <summary>A new game or the title screen replaces the game data without a load (RestraintHub's OnNewGame): drop the bar.</summary>
        [HideFromIl2Cpp]
        internal void OnNewGame()
        {
            if (Machine.Worn) Plugin.Logger.LogInfo("game data replaced without a load: spreader bar reset");
            Machine.Reset();
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

        // --- walking speed -----------------------------------------------------------------------------------------------
        // The game moves the player at MCommon's base speed (walk 1.1, crouch 0.6 m/s ...) times the Z / X scale (0.65 .. 1.15):
        // PlayerLocomotion.CurrentMoveSpeed 0.715 walking and 0.39 crouching at 0.65, 0.3575 walking with playerWalkSpeed
        // halved live [observed 2026-10-24]. While the bar is on it scales those base speeds, so Z / X keep working on top; they
        // go back when it comes off. The walk animation follows by itself: PlayerAnimationManager.locomotionMotionSpeed fell
        // by the same factor (0.677 -> 0.539 at x0.80) [observed 2026-10-24]. The spread-leg walk has a speed of its own, its
        // action record's FloatParam (0.45 m/s; set to 0.3 it walked at 0.3; Z / X leave it alone) [observed 2026-10-24].
        public static float SpeedFactor = 1f; // diagnostics: the factor applied
        private readonly ScaledValue _walkSpeed = new ScaledValue(), _crouchSpeed = new ScaledValue(), _gamanSpeed = new ScaledValue();
        private readonly ScaledValue _spreadWalkSpeed = new ScaledValue();

        private bool SlowWalking => Machine.Worn && (Plugin.DebugDisable.Value & 512) == 0;

        [HideFromIl2Cpp]
        private void UpdateSpeed()
        {
            var common = MCommon.Instance;
            if (common == null) return;
            if (SlowWalking)
            {
                // Over the widths the bar can take (MinWidthCm 8 still ends at the model's 15.2 cm).
                float narrowest = NarrowestCm();
                float f = WalkSlowdown.Factor(WidthNow, narrowest, Math.Max(narrowest, WidestCm()), Plugin.SpeedAtMinWidth.Value,
                                              Plugin.SpeedAtMaxWidth.Value);
                if (f != SpeedFactor) Plugin.Logger.LogInfo("[SFM-ACCEPT] speed: x" + f.ToString("0.00") + " at " + WidthNow.ToString("0.0") + " cm (" +
                                                       narrowest.ToString("0.0") + " .. " + WidestCm().ToString("0.0") + ")");
                SpeedFactor = f;
                common.playerWalkSpeed = _walkSpeed.Next(common.playerWalkSpeed, f);
                common.playerCrouchSpeed = _crouchSpeed.Next(common.playerCrouchSpeed, f);
                common.PlayerGamanWalkSpeed = _gamanSpeed.Next(common.PlayerGamanWalkSpeed, f);
                var spread = MAction.Get(ActionType.GanimataWalk);
                if (spread != null) spread.FloatParam = _spreadWalkSpeed.Next(spread.FloatParam, f);
            }
            else if (_walkSpeed.Active) ReleaseSpeed(common);
        }

        [HideFromIl2Cpp]
        private void ReleaseSpeed(MCommon common)
        {
            common.playerWalkSpeed = _walkSpeed.Release(common.playerWalkSpeed);
            common.playerCrouchSpeed = _crouchSpeed.Release(common.playerCrouchSpeed);
            common.PlayerGamanWalkSpeed = _gamanSpeed.Release(common.PlayerGamanWalkSpeed);
            var spread = MAction.Get(ActionType.GanimataWalk);
            if (spread != null && _spreadWalkSpeed.Active) spread.FloatParam = _spreadWalkSpeed.Release(spread.FloatParam);
            SpeedFactor = 1f;
            Plugin.Logger.LogInfo("[SFM-ACCEPT] speed: back to the game's (walk " + common.playerWalkSpeed.ToString("0.###") + ")");
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
            InteractHeld = r.Interact; // F after the hub (with the game's handcuffs on: only when they are the selected item)
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
                : r.Interact && GameStateFacade.IsAttachSomeHandcuffs() && !RestraintHub.Hub.InInteractArea ? "handcuff struggle"
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
            if (action.Type != ActionType.None || RestraintHub.Hub.MenuOpen || pca.PlayerState.IsCrouch || (cuffed && InteractHeld))
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
        // a thin slice at cuff height give the leg's cross-section (Logic/LimbGirth.cs). Kept relative to the shin bone, so the
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
            var (_, hx, hz) = LimbGirth.Extents(_outlineWorld, ankle, shinAxis, across);
            NVector3 want = LimbGirth.OvalScale(hx, hz, Plugin.ClearanceCm.Value / 100f, Model.Sides[side].HoleRadius, 0.3f, 2.5f,
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
        // LateUpdate before the leg changes with BakeMesh(mesh, true) the outline is a clean ring (baked in Camera.onPreCull
        // it did not match the bones at all). The bake still needs the renderer's lossy scale - pos + rot * (scale x v) - as
        // her body object sits under Manustia, scaled by her height (1.3125 on the first save); without it the outline came
        // out ~24 % small and pulled towards her feet [observed 2026-10-25, Binders]. Only its size and shape are kept - the
        // game moves the legs again after LateUpdate - and the cuff stays centred on the shin bone, which matches the drawn leg.
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
                Vector3 pos = t.position, scale = t.lossyScale;
                Quaternion rot = t.rotation;
                var world = new NVector3[verts.Length];
                for (int i = 0; i < verts.Length; i++) world[i] = N(pos + rot * Vector3.Scale(scale, verts[i]));
                var triArray = _bakeMesh.triangles;
                var tris = new int[triArray.Length];
                for (int i = 0; i < tris.Length; i++) tris[i] = triArray[i];
                int beforeR = ptsR.Count, beforeL = ptsL.Count;
                // Three cuts over the ring's height (the leg narrows towards the ankle); the widest decides.
                foreach (float h in SliceHeights)
                {
                    LimbGirth.Slice(world, tris, N(cR + axisR.normalized * h), N(axisR), near, ptsR);
                    LimbGirth.Slice(world, tris, N(cL + axisL.normalized * h), N(axisL), near, ptsL);
                }
                if (ptsR.Count > beforeR || ptsL.Count > beforeL)
                    sources += r.name + " R" + (ptsR.Count - beforeR) + "/L" + (ptsL.Count - beforeL) + "; ";
            }
            float maxR = Math.Min(0.09f, WidthNow / 100f * 0.45f); // never reach the other leg
            string meshes = MeshNames(sources);
            if (meshes != _girthMeshes) { _girthMeshes = meshes; _girth[0].Ok = _girth[1].Ok = false; } // other clothes: measure afresh
            Store(0, LimbGirth.Measure(ptsR, N(cR), N(axisR), 0.012f, maxR), kneeR);
            Store(1, LimbGirth.Measure(ptsL, N(cL), N(axisL), 0.012f, maxR), kneeL);
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
        private void Store(int side, LimbGirth.Fit fit, Transform shin)
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

        // --- helpers -----------------------------------------------------------------------------------------------------

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
            try { if (_walkSpeed.Active && MCommon.Instance != null) ReleaseSpeed(MCommon.Instance); }
            catch (Exception e) { Fail("walking speed", e); }
            if (_bakeMesh != null) Destroy(_bakeMesh);
            if (Instance == this) Instance = null;
        }
    }
}
