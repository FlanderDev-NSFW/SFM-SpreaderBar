namespace SfmKit.Pure
{
    /// <summary>
    /// A game value a mod scales by a factor without losing the game's own value (verified: unit tests in SpreaderBar.Tests;
    /// in game via SpreaderBar 0.8.0's walking speed, L3 2026-10-24): whatever the game writes (once, or again
    /// every frame) becomes the new base; a value equal to the mod's last write is the mod's own and keeps the base.
    /// </summary>
    internal sealed class ScaledValue
    {
        private float _base = float.NaN, _written = float.NaN;

        /// <summary>The game's own value (NaN before the first <see cref="Next"/>).</summary>
        public float Base => _base;

        /// <summary>True while the mod's value is written (after <see cref="Next"/>, until <see cref="Release"/>).</summary>
        public bool Active => !float.IsNaN(_written);

        /// <summary>The value to write this frame, given the one read from the game now.</summary>
        public float Next(float current, float factor)
        {
            if (!Active || current != _written) _base = current; // the game set it (again)
            _written = _base * factor;
            return _written;
        }

        /// <summary>Stop scaling: the value to write back (the game's own, or <paramref name="current"/> when the game has
        /// written a new one since).</summary>
        public float Release(float current)
        {
            float back = Active && current == _written ? _base : current;
            _written = float.NaN;
            return back;
        }
    }
}
