using System;

namespace SpreaderBar.Logic
{
    /// <summary>How much the bar slows the player: a factor on the game's own walk speed (which Z / X already scale), from
    /// <c>atMin</c> at the narrowest width to <c>atMax</c> at the widest, linear in between.</summary>
    public static class WalkSlowdown
    {
        /// <summary>The factor for <paramref name="widthCm"/>; widths outside the range use its ends.</summary>
        public static float Factor(float widthCm, float minCm, float maxCm, float atMin, float atMax)
        {
            if (!(maxCm > minCm)) return Math.Clamp(atMin, 0f, 1f);
            float t = Math.Clamp((widthCm - minCm) / (maxCm - minCm), 0f, 1f);
            return Math.Clamp(atMin + (atMax - atMin) * t, 0f, 1f);
        }
    }

    /// <summary>
    /// A game value the mod scales by a factor without losing the game's own value: whatever the game writes (once, or again
    /// every frame) becomes the new base; a value equal to the mod's last write is the mod's own and keeps the base.
    /// </summary>
    public sealed class ScaledValue
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
