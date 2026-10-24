using System;
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
        public const string SaveKeyShortcuts = "SpreaderBar.Shortcuts"; // written by the hub (kept from 0.6.0)

        public static void Apply(Harmony harmony)
        {
            int off = Plugin.DebugDisable.Value;
            var me = typeof(Patches);
            void Hook(int bit, System.Reflection.MethodBase target, string prefix = null, string postfix = null)
            {
                if ((off & bit) != 0) { Plugin.Logger.LogInfo("debug: hook " + bit + " off"); return; }
                harmony.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(me, prefix), postfix: postfix == null ? null : new HarmonyMethod(me, postfix));
            }
            // The input hook, ring label, unlock gauge and number shortcuts are RestraintHub's since 0.7.0 (bits 1, 4, 8 unused).
            Hook(16, AccessTools.Method(typeof(SaveManager), nameof(SaveManager.Load)), postfix: nameof(LoadPostfix));
            Hook(32, AccessTools.Method(typeof(SaveManager), nameof(SaveManager.Save)), postfix: nameof(SavePostfix));
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
                Plugin.Logger.LogInfo("[SFM-ACCEPT] save: locked=" + locked + " path=" + SaveRapper.currentDataPath);
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
                c.OnSaveLoaded(locked, (float)Math.Max(chance, 0.0));
                Plugin.Logger.LogInfo("[SFM-ACCEPT] load: locked=" + locked + " path=" + SaveRapper.currentDataPath);
            }
            catch (Exception e) { BarController.Fail("load hook", e); }
        }

        /// <summary>A save written by 0.1.0 (it has our chance key but no shortcut key) may hold the bar's ring index as a
        /// shortcut, which makes the game's ring setup throw and the stage never start. Point those keys at entry 0 again
        /// (before the stage starts) and give them back to the bar. The hub calls this when the save has no shortcut key.</summary>
        internal static int RepairShortcuts()
        {
            if (SaveRapper.Load(SaveKeyChance, -1.0) < 0) return 0; // not a SpreaderBar save at all
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
