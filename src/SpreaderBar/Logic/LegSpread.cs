using System;
using System.Numerics;

namespace SpreaderBar.Logic
{
    /// <summary>One leg after the animation (world space, Unity axes).</summary>
    public struct Leg
    {
        public Vector3 Hip, Knee, Ankle;
        /// <summary>Where the bar cuff sits: this far up the shin from the ankle (metres).</summary>
        public float CuffLift;

        public Vector3 Cuff => Ankle + BarPose.Unit(Knee - Ankle, Vector3.UnitY) * CuffLift;
    }

    /// <summary>Rotations to apply to one leg (world space, pre-multiplied): thigh.rotation = Thigh * thigh.rotation;
    /// shin.rotation = Thigh * Knee * shin.rotation (old shin rotation). The foot keeps its world rotation.</summary>
    public struct LegFix
    {
        public Quaternion Thigh, Knee;
        public bool Changed;
        public static LegFix None => new LegFix { Thigh = Quaternion.Identity, Knee = Quaternion.Identity };
    }

    /// <summary>
    /// Holds the ankles exactly one bar width apart, on top of whatever animation plays (pure maths, no Unity).
    /// The cuff-to-cuff vector keeps the animation's direction but at most <c>maxAngle</c> away from straight across the body
    /// (<c>rightToLeft</c>), so a stride stays a stride but crossed or closed legs open; its length is the bar width.
    /// Both cuffs move symmetrically about their middle; each leg follows with a two-bone solve (hip fixed, knee bends in its
    /// current plane, the thigh swings so the ankle lands on the target). If a target is out of a leg's reach (wide bar,
    /// straight legs) the hips are lowered just enough (<see cref="Result.HipDrop"/>) so the knees bend instead of the feet lifting.
    /// </summary>
    public static class LegSpread
    {
        public const float ReachUse = 0.985f; // never pull a leg completely straight

        public struct Result
        {
            public LegFix Right, Left;
            /// <summary>Lower the hips by this much along -up before applying the fixes (0 = not needed).</summary>
            public float HipDrop;
            /// <summary>Cuff distance across the body before / total cuff distance after.</summary>
            public float Before, After;
        }

        /// <summary>Where the left cuff should be relative to the right one: length <paramref name="width"/>, direction
        /// within <paramref name="maxAngle"/> (radians) of <paramref name="side"/>.</summary>
        public static Vector3 Constrain(Vector3 w, Vector3 side, float width, float maxAngle)
        {
            float len = w.Length();
            Vector3 dir = len > 1e-5f ? w / len : side;
            float cos = Vector3.Dot(dir, side);
            float cosMax = (float)Math.Cos(maxAngle);
            if (cos < cosMax)
            {
                Vector3 rest = dir - cos * side;
                rest = rest.LengthSquared() > 1e-10f ? Vector3.Normalize(rest) : Vector3.Zero;
                dir = cosMax * side + (float)Math.Sin(maxAngle) * rest;
                dir = rest == Vector3.Zero ? side : Vector3.Normalize(dir);
            }
            return dir * width;
        }

        public static Result Solve(Leg right, Leg left, float width, Vector3 rightToLeft, Vector3 up, float maxAngle = 0.5236f)
        {
            Vector3 side = BarPose.Unit(rightToLeft, Vector3.UnitX);
            up = BarPose.Unit(up, Vector3.UnitY);
            Vector3 cr = right.Cuff, cl = left.Cuff;
            Vector3 w = cl - cr;
            var result = new Result { Right = LegFix.None, Left = LegFix.None, Before = Vector3.Dot(w, side), After = w.Length() };
            Vector3 want = Constrain(w, side, width, maxAngle);
            if (Vector3.Distance(want, w) < 1e-4f) return result;

            // The cuffs sit up the shins, and the shins tilt as the ankles move: correct the shift a few times.
            Vector3 half = (want - w) * 0.5f, now = w;
            for (int i = 0; i < 6; i++)
            {
                Vector3 tr = right.Ankle - half, tl = left.Ankle + half;
                float drop = Math.Max(DropFor(right, tr, up), DropFor(left, tl, up));
                Leg r = Lower(right, up, drop), l = Lower(left, up, drop);
                result.Right = TwoBone(r, tr);
                result.Left = TwoBone(l, tl);
                result.HipDrop = drop;
                now = Apply(l, result.Left).Cuff - Apply(r, result.Right).Cuff;
                if (Vector3.Distance(now, want) < 5e-4f) break;
                half += (want - now) * 0.5f;
            }
            result.After = now.Length();
            return result;
        }

        /// <summary>How far the hip must come down (along -up) so <paramref name="target"/> is within reach.</summary>
        public static float DropFor(Leg leg, Vector3 target, Vector3 up)
        {
            float reach = (Vector3.Distance(leg.Hip, leg.Knee) + Vector3.Distance(leg.Knee, leg.Ankle)) * ReachUse;
            Vector3 d = target - leg.Hip;
            if (d.Length() <= reach) return 0f;
            float below = -Vector3.Dot(d, up);                      // how far the target is below the hip
            float across = (d + below * up).Length();               // horizontal part
            if (across >= reach || below <= 0f) return 0f;          // out of reach sideways or above the hip: nothing to gain
            return Math.Max(0f, below - (float)Math.Sqrt(reach * reach - across * across));
        }

        /// <summary>The leg moved down with the hips (the whole leg follows the pelvis).</summary>
        public static Leg Lower(Leg leg, Vector3 up, float drop)
        {
            if (drop <= 0f) return leg;
            Vector3 o = up * drop;
            return new Leg { Hip = leg.Hip - o, Knee = leg.Knee - o, Ankle = leg.Ankle - o, CuffLift = leg.CuffLift };
        }

        /// <summary>Two-bone solve: rotations that move the ankle to <paramref name="target"/> (or as close as the leg reaches).</summary>
        public static LegFix TwoBone(Leg leg, Vector3 target)
        {
            Vector3 a = leg.Hip, b = leg.Knee, c = leg.Ankle;
            float lab = Vector3.Distance(a, b), lbc = Vector3.Distance(b, c);
            if (lab < 1e-4f || lbc < 1e-4f) return LegFix.None;
            float reach = lab + lbc;
            float dt = Math.Clamp(Vector3.Distance(a, target), Math.Abs(lab - lbc) + 1e-4f, reach - 1e-4f);

            // 1. Knee: open/close the angle at the knee so hip-ankle has the target distance (bend plane unchanged).
            Vector3 u = a - b, v = c - b;
            Vector3 n = Vector3.Cross(u, v);
            var knee = Quaternion.Identity;
            if (n.LengthSquared() > 1e-10f) // a straight leg keeps its knee (only the thigh swings)
            {
                n = Vector3.Normalize(n);
                float now = Angle(u, v);
                float want = (float)Math.Acos(Math.Clamp((lab * lab + lbc * lbc - dt * dt) / (2f * lab * lbc), -1f, 1f));
                // Rotating v about n = u x v by a positive angle opens the knee.
                knee = Quaternion.CreateFromAxisAngle(n, want - now);
            }
            Vector3 c1 = b + Vector3.Transform(v, knee);

            // 2. Thigh: swing the hip->ankle line onto hip->target.
            var thigh = FromTo(c1 - a, target - a);
            return new LegFix { Thigh = thigh, Knee = knee, Changed = true };
        }

        /// <summary>The leg after a fix (for checks and tests).</summary>
        public static Leg Apply(Leg leg, LegFix fix)
        {
            if (!fix.Changed) return leg;
            Vector3 b = leg.Knee, c = leg.Ankle;
            Vector3 c1 = b + Vector3.Transform(c - b, fix.Knee);
            return new Leg
            {
                Hip = leg.Hip,
                Knee = leg.Hip + Vector3.Transform(b - leg.Hip, fix.Thigh),
                Ankle = leg.Hip + Vector3.Transform(c1 - leg.Hip, fix.Thigh),
                CuffLift = leg.CuffLift,
            };
        }

        public static float Angle(Vector3 a, Vector3 b)
        {
            float l = a.Length() * b.Length();
            return l < 1e-12f ? 0f : (float)Math.Acos(Math.Clamp(Vector3.Dot(a, b) / l, -1f, 1f));
        }

        /// <summary>Shortest rotation taking direction <paramref name="from"/> to <paramref name="to"/>.</summary>
        public static Quaternion FromTo(Vector3 from, Vector3 to)
        {
            from = BarPose.Unit(from, Vector3.UnitY);
            to = BarPose.Unit(to, Vector3.UnitY);
            float dot = Vector3.Dot(from, to);
            if (dot > 0.999999f) return Quaternion.Identity;
            if (dot < -0.999999f)
            {
                Vector3 axis = Vector3.Cross(from, Vector3.UnitX);
                if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(from, Vector3.UnitY);
                return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), (float)Math.PI);
            }
            Vector3 c = Vector3.Cross(from, to);
            return Quaternion.Normalize(new Quaternion(c, 1f + dot));
        }
    }
}
