using System;
using ExposureUnnoticed2.Object3D.Player.Scripts;
using ExposureUnnoticed2.ObjectUI.InGame.RingMenu;
using ExposureUnnoticed2.ObjectUI.InGame.TempUnlockPanel;
using ExposureUnnoticed2.Scripts.Base;
using ExposureUnnoticed2.Scripts.InGame;
using HarmonyLib;
using SfmKit.Pure;

namespace SpreaderBar
{
    /// <summary>Harmony hooks into the game. Every hook fails open: on an exception the game behaves as without the mod.</summary>
    internal static class Patches
    {
        public const string LabelKey = "spreaderbar_ring_label";
        public const string SaveKeyLocked = "SpreaderBar.Locked";
        public const string SaveKeyChance = "SpreaderBar.Chance";
        public const string SaveKeyShortcuts = "SpreaderBar.Shortcuts";

        public static void Apply(Harmony harmony)
        {
            int off = Plugin.DebugDisable.Value;
            var me = typeof(Patches);
            void Hook(int bit, System.Reflection.MethodBase target, string prefix = null, string postfix = null)
            {
                if ((off & bit) != 0) { Plugin.Logger.LogInfo("debug: hook " + bit + " off"); return; }
                harmony.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(me, prefix), postfix: postfix == null ? null : new HarmonyMethod(me, postfix));
            }
            Hook(1, AccessTools.Method(typeof(InputResolver), nameof(InputResolver.Resolve)), postfix: nameof(ResolvePostfix));
            // No hook on RingMenuParentView.OnInvokeCommandCommon: with it patched, saving crashed the game [observed 2026-10-11].
            // The controller detects ring clicks and number-shortcut taps itself (BarController.WatchRingChoice / WatchShortcuts).
            Hook(4, AccessTools.Method(typeof(Localizer), nameof(Localizer.Trans), new[] { typeof(string) }), postfix: nameof(TransPostfix));
            Hook(8, AccessTools.Method(typeof(TempUnlockPanelView), nameof(TempUnlockPanelView.OnLateUpdate)), postfix: nameof(UnlockPanelPostfix));
            Hook(16, AccessTools.Method(typeof(SaveManager), nameof(SaveManager.Load)), postfix: nameof(LoadPostfix));
            Hook(32, AccessTools.Method(typeof(SaveManager), nameof(SaveManager.Save)), postfix: nameof(SavePostfix));
        }

        // --- input: remember the interact key; while the bar is on the controller adjusts the input (BarController.OnInput) --

        private static void ResolvePostfix(InputData __result)
        {
            try
            {
                var c = BarController.Instance;
                if (c == null || __result == null) return;
                c.InteractHeld = __result.Interact;
                if (c.Machine.Worn) c.OnInput(__result);
            }
            catch (Exception e) { BarController.Fail("input hook", e); }
        }

        // --- ring label ---------------------------------------------------------------------------------------------------

        private static void TransPostfix(string key, ref string __result)
        {
            if (key == LabelKey) __result = Plugin.RingLabel.Value;
        }

        // --- the game's "Unlocking..." gauge shows our attempt too ----------------------------------------------------------

        private static void UnlockPanelPostfix(TempUnlockPanelView __instance)
        {
            try
            {
                var c = BarController.Instance;
                if (c == null || __instance == null) return;
                float attempt = c.Machine.Attempt;
                if (attempt <= 0f) return;
                if (__instance.contents != null && !__instance.contents.activeSelf) __instance.contents.SetActive(true);
                if (__instance.circleGauge != null) __instance.circleGauge.fillAmount = attempt;
            }
            catch (Exception e) { BarController.Fail("unlock gauge hook", e); }
        }

        // --- save / load: two extra keys in the game's own save file ------------------------------------------------------
        // Written right after SaveManager.Save, into the slot it just wrote (SaveRapper.currentDataPath). A hook on
        // SaveRapper.AfterProcess never ran (inlined?), and a Harmony finalizer on SaveManager.Save is avoided
        // [observed 2026-10-11].

        private static void SavePostfix()
        {
            try
            {
                var c = BarController.Instance;
                if (c == null || SaveRapper.IsSystemData || SaveRapper.currentDataPath == SaveRapper.SystemDataPath) return;
                bool locked = c.Machine.Worn && c.Machine.State != LockState.Opening;
                SaveRapper.Save(SaveKeyLocked, locked);
                SaveRapper.Save(SaveKeyChance, (double)c.Machine.Chance);
                SaveRapper.Save(SaveKeyShortcuts, c.ShortcutMask);
                Plugin.Logger.LogInfo("[SFM-ACCEPT] save: locked=" + locked + " shortcuts=" + c.ShortcutMask + " path=" + SaveRapper.currentDataPath);
            }
            catch (Exception e) { BarController.Fail("save hook", e); }
        }

        private static void LoadPostfix()
        {
            try
            {
                var c = BarController.Instance;
                if (c == null || SaveRapper.IsSystemData || SaveRapper.currentDataPath == SaveRapper.SystemDataPath) return; // system file (s.sd)
                bool locked = SaveRapper.Load(SaveKeyLocked, false);
                double chance = SaveRapper.Load(SaveKeyChance, -1.0);
                int shortcuts = SaveRapper.Load(SaveKeyShortcuts, -1);
                if (shortcuts < 0) shortcuts = chance >= 0 ? RepairShortcuts() : 0;
                c.OnSaveLoaded(locked, (float)Math.Max(chance, 0.0));
                c.OnShortcutsLoaded(shortcuts);
                Plugin.Logger.LogInfo("[SFM-ACCEPT] load: locked=" + locked + " shortcuts=" + shortcuts + " path=" + SaveRapper.currentDataPath);
            }
            catch (Exception e) { BarController.Fail("load hook", e); }
        }

        /// <summary>A save written by 0.1.0 (it has our chance key but no shortcut key) may hold the bar's ring index as a
        /// shortcut, which makes the game's ring setup throw and the stage never start. Point those keys at entry 0 again
        /// (before the stage starts) and give them back to the bar.</summary>
        private static int RepairShortcuts()
        {
            var data = GameState.GameStateData;
            if (data == null) return 0;
            int mask = 0;
            for (int k = 0; k < 8; k++)
            {
                if (BarController.ShortcutIndex(data, k) < BarController.VanillaRingCount) continue;
                BarController.SetShortcutIndex(data, k, 0);
                mask |= 1 << k;
            }
            if (mask != 0) Plugin.Logger.LogWarning("Save written by SpreaderBar 0.1.0 had the bar on a number shortcut (mask " + mask + "): repaired.");
            return mask;
        }
    }
}
