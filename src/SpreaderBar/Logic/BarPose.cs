using System;
using System.Numerics;

namespace SpreaderBar.Logic
{
    /// <summary>Where to draw the pieces this frame. Each group's mesh is stored relative to its own cuff's hole centre,
    /// so a group is drawn at Position with Rotation (and the model scale).</summary>
    public struct BarPoseResult
    {
        public Quaternion CuffRotationR, CuffRotationL, BarRotation;
        public Vector3 CuffPositionR, CuffPositionL, BarPositionR, BarPositionL;
        public float EyeDistance;   // between the cuff eyes, metres in the world
        public bool Clamped;        // the ankles were closer/further than the bar allows
    }

    /// <summary>
    /// Solves the bar for the current ankles (pure maths; Unity axes, left-handed, y up).
    /// Each cuff's hole axis (model y) is its shin (the ring is square to the leg) and the hole centre sits on the ankle. The cuff turns about its eye,
    /// where the chain holds it; the bar runs straight from eye to eye and telescopes (clamped to its range, then centred).
    /// </summary>
    public static class BarPose
    {
        public struct Input
        {
            public Vector3 AnkleR, AnkleL;      // hole centres go here (world)
            public Vector3 ShinR, ShinL;        // ankle -> knee (any length)
            public Vector3 Up;                  // fallback when a shin is unusable (e.g. world up)
            public Vector3 Forward;             // character forward: fallback bar direction is Up x Forward
        }

        public static BarPoseResult Solve(in Input input, CuffSide right, CuffSide left, float scale, float minHoleDistance, float maxHoleDistance)
            => Solve(input, right, left, new Vector3(scale), new Vector3(scale), scale, minHoleDistance, maxHoleDistance);

        public static BarPoseResult Solve(in Input input, CuffSide right, CuffSide left, float cuffScaleR, float cuffScaleL, float barScale,
                                          float minHoleDistance, float maxHoleDistance)
            => Solve(input, right, left, new Vector3(cuffScaleR), new Vector3(cuffScaleL), barScale, minHoleDistance, maxHoleDistance);

        /// <summary>Cuffs and bar may have different scales: each cuff is sized to its leg (<paramref name="cuffScaleR"/>,
        /// <paramref name="cuffScaleL"/>), the bar keeps <paramref name="barScale"/>; the bar's eyes land on the cuffs' eyes.</summary>
        public static BarPoseResult Solve(in Input input, CuffSide right, CuffSide left, Vector3 cuffScaleR, Vector3 cuffScaleL, float barScale,
                                          float minHoleDistance, float maxHoleDistance)
        {
            Vector3 up = Unit(input.Up, Vector3.UnitY);
            Vector3 fallbackX = Unit(Vector3.Cross(up, Unit(input.Forward, Vector3.UnitZ)), Vector3.UnitX);
            Vector3 shinR = Unit(input.ShinR, up), shinL = Unit(input.ShinL, up);
            // Eye relative to the hole, in model units (fixed by the model): x is along the bar.
            Vector3 eyeR = (right.Eye - right.Hole) * cuffScaleR, eyeL = (left.Eye - left.Hole) * cuffScaleL;
            Vector3 barEyeR = (right.Eye - right.Hole) * barScale, barEyeL = (left.Eye - left.Hole) * barScale;

            Vector3 x = Unit(input.AnkleL - input.AnkleR, fallbackX);
            Quaternion cuffR = Quaternion.Identity, cuffL = Quaternion.Identity;
            Vector3 eyeWorldR = input.AnkleR, eyeWorldL = input.AnkleL;
            for (int i = 0; i < 3; i++) // the eyes depend on the cuff turn, which depends on the bar direction
            {
                cuffR = FrameUp(shinR, x);
                cuffL = FrameUp(shinL, x);
                eyeWorldR = input.AnkleR + Vector3.Transform(eyeR, cuffR);
                eyeWorldL = input.AnkleL + Vector3.Transform(eyeL, cuffL);
                x = Unit(eyeWorldL - eyeWorldR, x);
            }

            // Bar roll: average shin, so both tubes stay coaxial.
            Quaternion bar = Frame(x, shinR + shinL);
            float eyeSpan = barEyeL.X - barEyeR.X; // the bar's eye distance minus its hole distance
            float minEye = minHoleDistance * barScale + eyeSpan, maxEye = maxHoleDistance * barScale + eyeSpan;
            float eyeDistance = Vector3.Distance(eyeWorldR, eyeWorldL);
            float clampedDistance = Math.Clamp(eyeDistance, Math.Min(minEye, maxEye), Math.Max(minEye, maxEye));
            bool clamped = Math.Abs(clampedDistance - eyeDistance) > 1e-5f;
            if (clamped)
            {
                Vector3 mid = (eyeWorldR + eyeWorldL) * 0.5f;
                eyeWorldR = mid - x * (clampedDistance * 0.5f);
                eyeWorldL = mid + x * (clampedDistance * 0.5f);
            }

            return new BarPoseResult
            {
                CuffRotationR = cuffR, CuffRotationL = cuffL, BarRotation = bar,
                CuffPositionR = input.AnkleR, CuffPositionL = input.AnkleL,
                // the bar group's eye (model) lands on the eye in the world
                BarPositionR = eyeWorldR - Vector3.Transform(barEyeR, bar),
                BarPositionL = eyeWorldL - Vector3.Transform(barEyeL, bar),
                EyeDistance = clampedDistance,
                Clamped = clamped,
            };
        }

        /// <summary>Rotation whose x axis is <paramref name="x"/> and whose y axis is as close to <paramref name="up"/> as
        /// possible (z = x cross y, Unity's forward for right x up).</summary>
        public static Quaternion Frame(Vector3 x, Vector3 up)
        {
            x = Unit(x, Vector3.UnitX);
            Vector3 y = up - Vector3.Dot(up, x) * x;
            if (y.LengthSquared() < 1e-8f) // up parallel to the bar: pick any perpendicular
                y = Math.Abs(x.Y) < 0.9f ? Vector3.UnitY - x.Y * x : Vector3.UnitZ - x.Z * x;
            y = Vector3.Normalize(y);
            Vector3 z = Vector3.Cross(x, y);
            // System.Numerics is row-vector: the rows are the images of the unit axes.
            var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
        }

        /// <summary>Rotation whose y axis is exactly <paramref name="up"/> (a cuff's hole axis along its shin) and whose x
        /// axis is as close to <paramref name="x"/> as possible (towards the other cuff).</summary>
        public static Quaternion FrameUp(Vector3 up, Vector3 x)
        {
            up = Unit(up, Vector3.UnitY);
            Vector3 xp = x - Vector3.Dot(x, up) * up;
            if (xp.LengthSquared() < 1e-8f) xp = Math.Abs(up.X) < 0.9f ? Vector3.UnitX - up.X * up : Vector3.UnitZ - up.Z * up;
            xp = Vector3.Normalize(xp);
            return Frame(xp, up); // xp is already perpendicular to up, so Frame keeps both exactly
        }

        public static Vector3 Unit(Vector3 v, Vector3 fallback)
        {
            float l = v.Length();
            if (!(l > 1e-6f) || float.IsInfinity(l)) return fallback;
            return v / l;
        }
    }
}
