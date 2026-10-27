using System;

namespace SfmKit.Pure
{
    internal enum LockState { Unlocked, Closing, Locked, Opening }

    /// <summary>
    /// Lock / unlock rules for a restraint, like the game's normal handcuffs [observed 2026-10-11, SpreaderBar; also used by
    /// WallRestraint]: each press of the unlock key is one
    /// attempt; a full attempt succeeds with a chance that starts at BaseChance (the mod's default 1: the first attempt
    /// frees; the game's handcuffs start at MCommon.HandcuffsProbability 0.1) and grows by ChancePerFailure per failure. Releasing the key early cancels the attempt; the next attempt
    /// needs a new press. Pure: time and the random roll come in from the caller.
    /// </summary>
    internal sealed class LockMachine
    {
        public float CloseSeconds = 0.6f, OpenSeconds = 0.6f, AttemptSeconds = 4.5f;
        public float BaseChance = 0.1f, ChancePerFailure = 0.02f;

        public LockState State { get; private set; } = LockState.Unlocked;
        /// <summary>0 = jaws open, 1 = closed (for the hinge animation).</summary>
        public float Closed { get; private set; }
        /// <summary>Progress of the current unlock attempt, 0..1 (0 when none).</summary>
        public float Attempt { get; private set; }
        public float Chance { get; private set; }
        public int Failures { get; private set; }

        private bool _needRelease;

        /// <summary>True while the restraint is on (closing, locked or opening).</summary>
        public bool Worn => State != LockState.Unlocked;

        /// <summary>Ring menu choice. Returns true if it started locking (it does nothing while worn, like the handcuffs).</summary>
        public bool RequestLock()
        {
            if (State != LockState.Unlocked) return false;
            State = LockState.Closing;
            Closed = 0f;
            Chance = BaseChance;
            Failures = 0;
            Attempt = 0f;
            _needRelease = true; // a key still held from before must be released first
            return true;
        }

        /// <summary>Restore a saved lock (no animation).</summary>
        public void Restore(bool locked, float chance)
        {
            State = locked ? LockState.Locked : LockState.Unlocked;
            Closed = locked ? 1f : 0f;
            Chance = locked ? Math.Clamp(float.IsNaN(chance) ? BaseChance : chance, BaseChance, 1f) : 0f;
            Failures = 0;
            Attempt = 0f;
            _needRelease = true;
        }

        public void Reset() => Restore(false, 0f);

        public enum Event { None, Locked, AttemptFailed, Unlocking, Unlocked }

        /// <summary>Advance by <paramref name="dt"/> seconds. <paramref name="unlockHeld"/>: the unlock key is held and may be used
        /// (no other interaction claims it). <paramref name="roll"/>: uniform random in [0,1), used when an attempt completes.</summary>
        public Event Tick(float dt, bool unlockHeld, float roll)
        {
            if (!(dt > 0f)) dt = 0f;
            switch (State)
            {
                case LockState.Closing:
                    Closed = Math.Min(1f, Closed + dt / CloseSeconds);
                    if (!unlockHeld) _needRelease = false;
                    if (Closed >= 1f) { State = LockState.Locked; return Event.Locked; }
                    return Event.None;

                case LockState.Locked:
                    if (!unlockHeld)
                    {
                        _needRelease = false;
                        Attempt = 0f;
                        return Event.None;
                    }
                    if (_needRelease) return Event.None;
                    Attempt = Math.Min(1f, Attempt + dt / AttemptSeconds);
                    if (Attempt < 1f) return Event.None;
                    Attempt = 0f;
                    _needRelease = true;
                    if (roll < Math.Max(Chance, BaseChance)) // BaseChance may be raised live (config)
                    {
                        State = LockState.Opening;
                        return Event.Unlocking;
                    }
                    Failures++;
                    Chance = Math.Min(1f, Chance + ChancePerFailure);
                    return Event.AttemptFailed;

                case LockState.Opening:
                    Closed = Math.Max(0f, Closed - dt / OpenSeconds);
                    if (Closed <= 0f) { State = LockState.Unlocked; return Event.Unlocked; }
                    return Event.None;
            }
            return Event.None;
        }
    }
}
