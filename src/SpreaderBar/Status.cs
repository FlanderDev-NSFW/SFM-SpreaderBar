using SpreaderBar.Logic;
using SfmKit.Pure;

namespace SpreaderBar
{
    /// <summary>Read-only view of the mod's state for diagnostics (e.g. ./tools/dev.ps1 game get SpreaderBar.Status::State).</summary>
    public static class Status
    {
        public static string State = "Unlocked";
        public static float Closed, Attempt, Chance;
        public static int Failures;
        public static bool InteractHeld, UnlockAllowed;

        internal static void Update(LockMachine m, bool interactHeld, bool unlockAllowed)
        {
            State = m.State.ToString();
            Closed = m.Closed;
            Attempt = m.Attempt;
            Chance = m.Chance;
            Failures = m.Failures;
            InteractHeld = interactHeld;
            UnlockAllowed = unlockAllowed;
        }
    }
}
