using System;
using ExposureUnnoticed2.Scripts.Base;
using ExposureUnnoticed2.Scripts.InGame;
using RestraintHub;
using SfmKit.Pure;

namespace SpreaderBar
{
    /// <summary>The bar's save data, written and read through RestraintHub (OnSave / OnLoad), and the repair of 0.1.0 saves.</summary>
    internal static class Patches
    {
        public const string LabelKey = "spreaderbar_ring_label";
        public const string SaveKeyLocked = "SpreaderBar.Locked";
        public const string SaveKeyChance = "SpreaderBar.Chance";
        public const string SaveKeyShortcuts = "SpreaderBar.Shortcuts"; // written by the hub (kept from 0.6.0)

        // --- save / load: two extra keys in the game's own save file ------------------------------------------------------
        // Called by RestraintHub (Restraint.OnSave / OnLoad) right after the game wrote / read a save slot (never the system file).

        internal static void Save(SaveSlot slot)
        {
            try
            {
                var c = BarController.Instance;
                if (c == null) return;
                bool locked = c.Machine.Worn && c.Machine.State != LockState.Opening;
                slot.Save(SaveKeyLocked, locked);
                slot.Save(SaveKeyChance, (double)c.Machine.Chance);
                Plugin.Logger.LogInfo("[SFM-ACCEPT] save: locked=" + locked + " path=" + slot.Path);
            }
            catch (Exception e) { BarController.Fail("save hook", e); }
        }

        internal static void Load(SaveSlot slot)
        {
            try
            {
                var c = BarController.Instance;
                if (c == null) return;
                bool locked = slot.Load(SaveKeyLocked, false);
                double chance = slot.Load(SaveKeyChance, -1.0);
                c.OnSaveLoaded(locked, (float)Math.Max(chance, 0.0));
                Plugin.Logger.LogInfo("[SFM-ACCEPT] load: locked=" + locked + " path=" + slot.Path);
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
