using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using RestraintHub;
using SpreaderBar.Logic;

namespace SpreaderBar
{
    /// <summary>
    /// Spreader bar: a leg restraint in the ring menu (always owned). Locked, it holds the ankles apart (or, in SpreadLegWalk
    /// mode, the player in the spread-leg walk) until it is unlocked by holding the interact key (one try by default).
    /// </summary>
    public enum BarMode { LegsApart, SpreadLegWalk }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(Hub.Guid, "0.3.0")] // OnSave / OnLoad / OnNewGame
    public sealed class Plugin : BasePlugin
    {
        public const string PluginGuid = "com.local.spreaderbar";
        public const string PluginName = "SpreaderBar";
        public const string PluginVersion = "0.8.2";
        private const string ModelResource = "SpreaderBar.assets.spreaderbar.sbm";

        internal static ManualLogSource Logger;
        /// <summary>The bar's registration with RestraintHub (ring entry, selection, F and + / -).</summary>
        internal static Restraint Entry;
        public static ConfigEntry<string> RingLabel;
        public static ConfigEntry<BarMode> Mode;
        public static ConfigEntry<bool> FitCuffs;
        public static ConfigEntry<int> DebugDisable;
        public static ConfigEntry<float> Scale, ClearanceCm, MaxOval, StrideAngle, WidthCm, MinWidthCm, MaxWidthCm, StepCm, CuffHeight, AttemptSeconds, SuccessChance, SpeedAtMinWidth, SpeedAtMaxWidth; // public: tunable live with the bridge (game set)

        public override void Load()
        {
            Logger = Log;
            DebugDisable = Config.Bind("Debug", "Disable", 0, "Diagnostics: switch parts off to find a conflict. Bitmask: 128 the whole controller, 256 leg spreading (live), 512 slower walking (live). (1, 4, 8: hooks now in RestraintHub; 16, 32: save and load go through RestraintHub since 0.8.2.)");
            RingLabel = Config.Bind("General", "RingLabel", "Spreader Bar", "Name of the ring menu entry.");
            Mode = Config.Bind("General", "Mode", BarMode.LegsApart,
                "LegsApart: the bar only holds the ankles apart, on top of any animation or pose. " +
                "SpreadLegWalk: the player is also held in the Spread-Leg Walk pose (other actions still work, the pose returns after).");
            AttemptSeconds = Config.Bind("Unlock", "AttemptSeconds", 4.5f,
                new ConfigDescription("Seconds the interact key must be held for one unlock attempt.",
                    new AcceptableValueRange<float>(0.3f, 10f)));
            SuccessChance = Config.Bind("Unlock", "SuccessChance", 1f,
                new ConfigDescription("Chance that a full unlock attempt frees the legs (1 = always the first try). Below 1, every " +
                                      "failed attempt adds the game's handcuff increase (MCommon.HandcuffsAddProbabilityByFailure, 0.02).",
                    new AcceptableValueRange<float>(0.01f, 1f)));
            Scale = Config.Bind("Model", "Scale", 0.75f,
                new ConfigDescription("Size of the bar and cuffs relative to the Blender model (the player avatar is scaled 0.9).",
                    new AcceptableValueRange<float>(0.3f, 2f)));
            WidthCm = Config.Bind("Bar", "WidthCm", 25f,
                "Distance between the cuff centres in game centimetres; the ankles are held exactly this far apart in every pose. " +
                "Changed in game with + / - while the bar is on (saved here).");
            MinWidthCm = Config.Bind("Bar", "MinWidthCm", 16f, new ConfigDescription("Narrowest width + / - can set.", new AcceptableValueRange<float>(8f, 60f)));
            MaxWidthCm = Config.Bind("Bar", "MaxWidthCm", 60f,
                new ConfigDescription("Widest width + / - can set (also limited by the model's full tube length).", new AcceptableValueRange<float>(15f, 150f)));
            StrideAngle = Config.Bind("Bar", "StrideAngle", 30f,
                new ConfigDescription("How far (degrees) the bar may turn away from straight across the body when one foot is ahead " +
                                      "(walking, stances). 0 = always side by side.", new AcceptableValueRange<float>(0f, 80f)));
            StepCm = Config.Bind("Bar", "StepCm", 1f, new ConfigDescription("Width change per + / - press (holding repeats).", new AcceptableValueRange<float>(0.2f, 10f)));
            SpeedAtMinWidth = Config.Bind("Walk", "SpeedAtMinWidth", 0.35f,
                new ConfigDescription("While the bar is on, walking and crouching are slower: the game's speed (the one Z / X set) times " +
                                      "this at the narrowest width (MinWidthCm, or the model's limit). Applies live.", new AcceptableValueRange<float>(0.05f, 1f)));
            SpeedAtMaxWidth = Config.Bind("Walk", "SpeedAtMaxWidth", 0.6f,
                new ConfigDescription("The same factor at the widest width (MaxWidthCm, or the model's limit); linear in between. " +
                                      "Both 1 = no slowdown.", new AcceptableValueRange<float>(0.05f, 1f)));
            FitCuffs = Config.Bind("Model", "FitCuffs", true,
                "Size each cuff to the leg it closes around (measured from the body, socks and shoes every 2 s). Off: cuffs use Scale.");
            ClearanceCm = Config.Bind("Model", "ClearanceCm", 0.4f,
                new ConfigDescription("Gap between the leg and the cuff's inner ring when FitCuffs is on.", new AcceptableValueRange<float>(0f, 3f)));
            MaxOval = Config.Bind("Model", "MaxOval", 1.3f,
                new ConfigDescription("How oval a fitted cuff may get (long / short side) to hug an oval leg; 1 = always round " +
                                      "(then loose on the narrow side).", new AcceptableValueRange<float>(1f, 2f)));
            CuffHeight = Config.Bind("Model", "CuffHeight", 0.085f,
                new ConfigDescription("How far up the shin from the ankle joint the cuffs sit, in metres.",
                    new AcceptableValueRange<float>(-0.05f, 0.15f)));

            try
            {
                BarController.Model = LoadModel();
            }
            catch (Exception e)
            {
                Logger.LogError("Spreader bar model could not be loaded, the mod is disabled: " + e.Message);
                return;
            }

            ClassInjector.RegisterTypeInIl2Cpp<BarController>();
            if ((DebugDisable.Value & 128) == 0) AddComponent<BarController>();
            Entry = new Restraint
            {
                Id = "SpreaderBar",
                RingValue = BarController.RingItemValue,
                Label = () => RingLabel.Value,
                ShortcutSaveKey = Patches.SaveKeyShortcuts,
                OnSave = Patches.Save,
                OnLoad = Patches.Load,
                OnNewGame = () => BarController.Instance?.OnNewGame(),
                OnShortcutsMissing = Patches.RepairShortcuts,
                Holds = BodyParts.Legs,
                IsWorn = () => BarController.Instance != null && BarController.Instance.Machine.Worn,
                // No running; the rest (yielding the forced pose to actions, no running in it) is BarController.OnInput.
                Blocks = () => RestraintHub.Blocks.Running,
                OnChosen = () => { var c = BarController.Instance; if (c != null) c.OnRingChosen(); },
                Attempt = () => BarController.Instance == null ? 0f : BarController.Instance.Machine.Attempt,
                OnInput = i => { var c = BarController.Instance; if (c != null) c.OnInput(i); },
                Controls = "+/- width",
            };
            Hub.Register(Entry);
            Logger.LogInfo("[SFM-ACCEPT] load: " + PluginName + " " + PluginVersion + " ready (bar " + BarController.WidthNow.ToString("0.0") +
                           " cm, + / - from " + MinWidthCm.Value.ToString("0") + " to " + BarController.WidestCm().ToString("0.0") + " cm)");
        }

        private static BarModel LoadModel()
        {
            using Stream s = typeof(Plugin).Assembly.GetManifestResourceStream(ModelResource)
                             ?? throw new FileNotFoundException("embedded resource " + ModelResource + " missing");
            var model = BarModel.Read(s);
            model.ToUnity();
            // The tubes are refitted for each bar width (BarController.ApplyWidth); keep their original shape.
            BarController.TubeOriginals = BarFit.SaveTubes(model);
            BarController.FullLengthHoleMax = BarFit.FullLengthMax(model, BarController.TubeOriginals);
            BarController.ShortestHoleMin = BarFit.FitFor(model, BarController.TubeOriginals, 0f).Min; // refitted for the width below
            BarController.FitModel(model, WidthCm.Value);
            return model;
        }
    }
}
