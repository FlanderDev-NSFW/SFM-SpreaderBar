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
}
