using System;
using System.Numerics;

namespace SpreaderBar.Logic
{
    /// <summary>
    /// Fitting the telescoping bar to the pose (all in model metres, bar along x, Unity axes).
    /// The right tube is the outer one: it starts at the right cuff ('attach' end) and its open end points to +x.
    /// The left (inner) tube starts at the left cuff and its open end points to -x. Shortening keeps the end bands
    /// (caps, bevels) and squeezes only the plain middle of each tube.
    /// </summary>
    public static class BarFit
    {
        public const float EndBand = 0.012f;    // tube ends kept unscaled (caps and bevels sit within ~1 cm)
        public const float MinOverlap = 0.01f;  // inner tube stays this far inside the outer one

        public readonly struct Ends
        {
            public readonly float Attach, Open;
            public Ends(float attach, float open) { Attach = attach; Open = open; }
            public float Length => Math.Abs(Open - Attach);
        }

        public static Ends TubeEnds(BarModel m, int side)
        {
            var tube = m.Tube(side) ?? throw new InvalidOperationException("model has no bar tube for side " + side);
            float min = float.MaxValue, max = float.MinValue;
            foreach (var p in tube.Positions) { min = Math.Min(min, p.X); max = Math.Max(max, p.X); }
            return side == 0 ? new Ends(min, max) : new Ends(max, min);
        }

        /// <summary>Distance between the two hole centres for which the tubes still overlap and neither tube runs
        /// past the other's attach end.</summary>
        public static (float Min, float Max) HoleDistanceRange(BarModel m)
        {
            Ends r = TubeEnds(m, 0), l = TubeEnds(m, 1);
            float hr = m.Sides[0].Hole.X, hl = m.Sides[1].Hole.X;
            float max = r.Open - hr - l.Open + hl - MinOverlap;
            float minInner = r.Attach - hr + EndBand - l.Open + hl;   // inner open end stays clear of the outer attach band
            float minOuter = r.Open - hr - l.Attach + hl + EndBand;   // outer open end stays clear of the inner attach band
            return (Math.Max(minInner, minOuter), max);
        }

        /// <summary>Tube length (both tubes) that makes <paramref name="maxDistance"/> the longest hole distance.</summary>
        public static float TubeLengthFor(BarModel m, float maxDistance)
        {
            Ends r = TubeEnds(m, 0), l = TubeEnds(m, 1);
            float hr = m.Sides[0].Hole.X, hl = m.Sides[1].Hole.X;
            return (maxDistance - r.Attach + hr + l.Attach - hl + MinOverlap) / 2f;
        }

        /// <summary>Copies of both tubes' vertex positions (to refit the bar for another width later).</summary>
        public static Vector3[][] SaveTubes(BarModel m) => new[] { (Vector3[])m.Tube(0).Positions.Clone(), (Vector3[])m.Tube(1).Positions.Clone() };

        /// <summary>Longest hole distance the tubes allow at full length (model metres).</summary>
        public static float FullLengthMax(BarModel m, Vector3[][] original)
        {
            var saved = SaveTubes(m);
            Restore(m, original);
            float max = HoleDistanceRange(m).Max;
            Restore(m, saved);
            return max;
        }

        /// <summary>Refit the tubes from their original shape so <paramref name="holeDistance"/> (model metres) lies in the
        /// range, a little below the longest. Returns the new range.</summary>
        public static (float Min, float Max) FitFor(BarModel m, Vector3[][] original, float holeDistance, float slack = 0.02f)
        {
            Restore(m, original);
            ShortenTubes(m, TubeLengthFor(m, holeDistance + slack));
            return HoleDistanceRange(m);
        }

        private static void Restore(BarModel m, Vector3[][] tubes)
        {
            for (int side = 0; side < 2; side++) Array.Copy(tubes[side], m.Tube(side).Positions, tubes[side].Length);
        }

        /// <summary>Shortens (never lengthens) both tubes to <paramref name="length"/>: end bands move rigidly, the middle
        /// is squeezed linearly. Returns the length actually used (at least two end bands plus 5 mm).</summary>
        public static float ShortenTubes(BarModel m, float length)
        {
            float used = length;
            for (int side = 0; side < 2; side++)
            {
                Ends e = TubeEnds(m, side);
                float target = Math.Max(Math.Min(length, e.Length), 2 * EndBand + 0.005f);
                used = target;
                float dir = side == 0 ? 1f : -1f; // from attach towards open
                float middle = e.Length - 2 * EndBand, newMiddle = target - 2 * EndBand;
                var tube = m.Tube(side);
                for (int i = 0; i < tube.Positions.Length; i++)
                {
                    var p = tube.Positions[i];
                    float t = (p.X - e.Attach) * dir; // distance from the attach end along the tube
                    float nt;
                    if (t <= EndBand) nt = t;
                    else if (t >= e.Length - EndBand) nt = t - (e.Length - target);
                    else nt = EndBand + (t - EndBand) * (middle > 1e-6f ? newMiddle / middle : 0f);
                    tube.Positions[i] = new Vector3(e.Attach + nt * dir, p.Y, p.Z);
                }
            }
            return used;
        }
    }
}
