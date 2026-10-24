using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using SpreaderBar.Logic;
using SfmKit.Pure;

namespace SpreaderBar.Tests
{
    internal static class Program
    {
        private static int _passed, _failed;

        private static void Check(bool ok, string name)
        {
            if (ok) { _passed++; Console.WriteLine("  PASS  " + name); }
            else { _failed++; Console.WriteLine("  FAIL  " + name); }
        }

        private static bool Near(float a, float b, float eps = 1e-4f) => Math.Abs(a - b) <= eps;
        private static bool Near(Vector3 a, Vector3 b, float eps = 1e-4f) => Vector3.Distance(a, b) <= eps;
        private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        private static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);

        private static int Main()
        {
            string modelPath = Path.Combine(AppContext.BaseDirectory, "spreaderbar.sbm");
            if (File.Exists(modelPath)) { ModelTests(modelPath); RefitTests(modelPath); }
            else Console.WriteLine("  SKIP  model tests (" + modelPath + " missing: export it with mods/SpreaderBar/tools/export_model.py)");
            FormatErrorTests();
            ConversionTests();
            PoseTests();
            LockTests();
            LegTests();
            GirthTests();
            PoseCuffScaleTests();
            SlowdownTests();

            Console.WriteLine(_passed + " passed, " + _failed + " failed");
            return _failed == 0 ? 0 : 1;
        }

        private static BarModel Load(string path)
        {
            using var f = File.OpenRead(path);
            return BarModel.Read(f);
        }

        private static void ModelTests(string path)
        {
            var m = Load(path);
            Check(m.Parts.Count == 8, "model: 8 rigid parts (tube, chain, cuff, jaw per side)");
            Check(m.Sides[0].Hole.X < 0 && m.Sides[1].Hole.X > 0, "model: right cuff at -x, left at +x");
            Check(m.Sides[0].HoleRadius > 0.02f && m.Sides[0].HoleRadius < 0.06f, "model: ankle hole radius is a few cm");
            Check(m.Tube(0) != null && m.Tube(1) != null, "model: both tubes present");

            // Winding: numerically, cross(b-a, c-a) points along the outward normal for front faces in both Blender and
            // Unity (Unity's RecalculateNormals uses the same formula). Mirroring positions flips that sign, so ToUnity must
            // flip the index order to keep the agreement positive.
            float before = WindingAgreement(m);
            m.ToUnity();
            float after = WindingAgreement(m);
            Check(before > 0.9f, "model: Blender winding agrees with normals (" + before.ToString("0.00") + ")");
            Check(after > 0.9f, "model: after the mirror (and index flip) winding still agrees with normals (" + after.ToString("0.00") + ")");

            var (min0, max0) = BarFit.HoleDistanceRange(m);
            Check(max0 > min0 && Near(max0 - min0, m.SlideRange, 0.06f), "fit: unshortened telescope range ~ the rig's slide range (" + min0.ToString("0.000") + ".." + max0.ToString("0.000") + ")");

            float wantMax = 0.32f;
            float length = BarFit.TubeLengthFor(m, wantMax);
            var outerBefore = BarFit.TubeEnds(m, 0);
            var capBefore = CountNear(m.Tube(0), outerBefore.Attach, BarFit.EndBand);
            float used = BarFit.ShortenTubes(m, length);
            var (min1, max1) = BarFit.HoleDistanceRange(m);
            Check(Near(used, length, 1e-4f) && Near(max1, wantMax, 1e-3f), "fit: shortened tubes give the requested longest distance (" + max1.ToString("0.000") + ")");
            Check(min1 < max1 && min1 > 0.2f, "fit: shortened range stays valid (" + min1.ToString("0.000") + ".." + max1.ToString("0.000") + ")");
            Check(Near(BarFit.TubeEnds(m, 0).Attach, outerBefore.Attach, 1e-5f), "fit: the attach end does not move");
            Check(CountNear(m.Tube(0), outerBefore.Attach, BarFit.EndBand) == capBefore, "fit: the attach end band keeps all its vertices");
            float again = BarFit.ShortenTubes(m, 10f);
            Check(Near(BarFit.TubeEnds(m, 0).Length, used, 1e-4f) && again <= used + 1e-4f, "fit: never lengthens");
        }

        private static int CountNear(ModelPart p, float x, float band)
        {
            int n = 0;
            foreach (var v in p.Positions) if (Math.Abs(v.X - x) <= band) n++;
            return n;
        }

        /// <summary>Mean sign of dot(geometric normal, stored normal) over all triangles (right-handed cross product).</summary>
        private static float WindingAgreement(BarModel m)
        {
            double sum = 0; int n = 0;
            foreach (var p in m.Parts)
                for (int i = 0; i < p.Indices.Length; i += 3)
                {
                    Vector3 a = p.Positions[p.Indices[i]], b = p.Positions[p.Indices[i + 1]], c = p.Positions[p.Indices[i + 2]];
                    Vector3 g = Vector3.Cross(b - a, c - a);
                    if (g.LengthSquared() < 1e-14f) continue;
                    Vector3 sn = p.Normals[p.Indices[i]] + p.Normals[p.Indices[i + 1]] + p.Normals[p.Indices[i + 2]];
                    sum += Math.Sign(Vector3.Dot(g, sn)); n++;
                }
            return n == 0 ? 0 : (float)(sum / n);
        }

        private static void FormatErrorTests()
        {
            bool Throws(byte[] data)
            {
                try { BarModel.Read(new MemoryStream(data)); return false; }
                catch (InvalidDataException) { return true; }
                catch (EndOfStreamException) { return true; }
            }
            Check(Throws(new byte[] { 1, 2, 3, 4, 1, 0, 0, 0 }), "format: wrong magic rejected");
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(BarModel.Magic); w.Write(2u);
            Check(Throws(ms.ToArray()), "format: unknown version rejected");
            ms = new MemoryStream(); w = new BinaryWriter(ms);
            w.Write(BarModel.Magic); w.Write(1u); w.Write(0.35f);
            Check(Throws(ms.ToArray()), "format: truncated file rejected");
            // one part with an index pointing outside its vertices
            ms = new MemoryStream(); w = new BinaryWriter(ms);
            w.Write(BarModel.Magic); w.Write(1u); w.Write(0.35f);
            for (int s = 0; s < 2; s++) for (int k = 0; k < 14; k++) w.Write(0f);
            w.Write(1u); w.Write((byte)0); w.Write((byte)0); w.Write(1f); w.Write(1f); w.Write(1f);
            w.Write(1u); for (int k = 0; k < 6; k++) w.Write(0f);
            w.Write(3u); w.Write(0u); w.Write(0u); w.Write(5u);
            Check(Throws(ms.ToArray()), "format: out-of-range index rejected");
        }

        private static void ConversionTests()
        {
            // A rotation about a in Blender space must match a rotation about the converted axis/angle in Unity space.
            var side = new CuffSide { HingeAxis = Vector3.Normalize(new Vector3(0.2f, -0.3f, 0.9f)), HingeAngle = 1.1f, HingePivot = new Vector3(0.1f, 0.2f, 0.3f) };
            Vector3 p = new Vector3(0.4f, -0.1f, 0.25f);
            Vector3 rotatedBlender = Vector3.Transform(p - side.HingePivot, Quaternion.CreateFromAxisAngle(side.HingeAxis, side.HingeAngle)) + side.HingePivot;
            var m = new BarModel();
            m.Sides[0] = side;
            m.Sides[1] = new CuffSide();
            m.ToUnity();
            Vector3 pu = BarModel.Swap(p);
            Vector3 rotatedUnity = Vector3.Transform(pu - side.HingePivot, Quaternion.CreateFromAxisAngle(side.HingeAxis, side.HingeAngle)) + side.HingePivot;
            Check(Near(rotatedUnity, BarModel.Swap(rotatedBlender)), "convert: hinge rotation survives the axis mirror");
        }

        private static CuffSide Cuff(float holeX, float eyeX) => new CuffSide { Hole = new Vector3(holeX, 0, 0), Eye = new Vector3(eyeX, 0, 0), HoleRadius = 0.03f };

        private static void PoseTests()
        {
            var q = BarPose.Frame(new Vector3(1, 0, 0), new Vector3(0, 1, 0));
            Check(Near(Vector3.Transform(Vector3.UnitX, q), Vector3.UnitX) && Near(Vector3.Transform(Vector3.UnitY, q), Vector3.UnitY), "frame: identity for x right, y up");
            q = BarPose.Frame(new Vector3(0, 0, 2), new Vector3(0.3f, 1, 0.5f));
            Vector3 fx = Vector3.Transform(Vector3.UnitX, q), fy = Vector3.Transform(Vector3.UnitY, q), fz = Vector3.Transform(Vector3.UnitZ, q);
            Check(Near(fx, Vector3.UnitZ) && Near(Vector3.Dot(fx, fy), 0) && Near(Vector3.Cross(fx, fy), fz), "frame: x exact, orthonormal, right-handed maths (z = x cross y)");
            q = BarPose.Frame(new Vector3(0, 1, 0), new Vector3(0, 1, 0));
            Check(Finite(q) && Near(Vector3.Transform(Vector3.UnitX, q), Vector3.UnitY), "frame: up parallel to x still gives a rotation");

            CuffSide right = Cuff(-0.45f, -0.38f), left = Cuff(0.45f, 0.38f);
            var input = new BarPose.Input
            {
                AnkleR = new Vector3(-0.13f, 0.05f, 0), AnkleL = new Vector3(0.13f, 0.05f, 0),
                ShinR = Vector3.UnitY, ShinL = Vector3.UnitY, Up = Vector3.UnitY, Forward = Vector3.UnitZ,
            };
            // eye offset 0.07 * scale 1 each side -> eye distance = 0.26 - 0.14 = 0.12 for hole distance 0.26
            var r = BarPose.Solve(input, right, left, 1f, 0.2f, 0.3f);
            Check(!r.Clamped && Near(r.EyeDistance, 0.12f), "pose: straight legs, distance in range, eye distance = holes - eye offsets");
            Check(Near(Vector3.Transform(Vector3.UnitX, r.BarRotation), Vector3.UnitX) && Near(Vector3.Transform(Vector3.UnitY, r.CuffRotationR), Vector3.UnitY), "pose: bar along the ankles, hole axis up");
            Check(Near(r.CuffPositionR, input.AnkleR) && Near(r.CuffPositionL, input.AnkleL), "pose: holes on the ankles");
            Check(Near(r.BarPositionR, input.AnkleR) && Near(r.BarPositionL, input.AnkleL), "pose: untilted cuffs -> bar groups coincide with cuff groups");

            // shins leaning outwards and forwards (squat): cuffs tilt, eyes of bar and cuff groups still meet
            input.ShinR = new Vector3(-0.3f, 1f, 0.4f);
            input.ShinL = new Vector3(0.3f, 1f, 0.4f);
            r = BarPose.Solve(input, right, left, 1f, 0.1f, 0.4f);
            Vector3 eyeCuffR = r.CuffPositionR + Vector3.Transform(right.Eye - right.Hole, r.CuffRotationR);
            Vector3 eyeBarR = r.BarPositionR + Vector3.Transform(right.Eye - right.Hole, r.BarRotation);
            Vector3 eyeCuffL = r.CuffPositionL + Vector3.Transform(left.Eye - left.Hole, r.CuffRotationL);
            Vector3 eyeBarL = r.BarPositionL + Vector3.Transform(left.Eye - left.Hole, r.BarRotation);
            Check(Near(eyeCuffR, eyeBarR, 1e-3f) && Near(eyeCuffL, eyeBarL, 1e-3f), "pose: tilted shins, chain eyes stay joined");
            Vector3 holeAxisR = Vector3.Transform(Vector3.UnitY, r.CuffRotationR);
            Check(Near(holeAxisR, Vector3.Normalize(input.ShinR)), "pose: right hole axis is the shin (ring square to the leg)");
            q = BarPose.FrameUp(new Vector3(0, 1, 0), new Vector3(0, 1, 0));
            Check(Finite(q) && Near(Vector3.Transform(Vector3.UnitY, q), Vector3.UnitY), "frame up: bar hint parallel to the shin still gives a rotation");
            Vector3 barX = Vector3.Transform(Vector3.UnitX, r.BarRotation);
            Check(Near(Vector3.Dot(barX, Vector3.Normalize(eyeBarL - eyeBarR)), 1f, 1e-3f), "pose: bar runs straight from eye to eye");

            // ankles too far apart: bar at its longest, centred between the eyes
            input.ShinR = input.ShinL = Vector3.UnitY;
            input.AnkleR = new Vector3(-0.5f, 0, 0); input.AnkleL = new Vector3(0.5f, 0, 0);
            r = BarPose.Solve(input, right, left, 1f, 0.2f, 0.3f);
            Check(r.Clamped && Near(r.EyeDistance, 0.3f - 0.14f), "pose: too far -> clamped to the longest bar");
            Check(Near((r.BarPositionR + r.BarPositionL) * 0.5f, Vector3.Zero), "pose: clamped bar stays centred");
            // scale applies to eye offsets and range
            input.AnkleR = new Vector3(-0.1f, 0, 0); input.AnkleL = new Vector3(0.1f, 0, 0);
            r = BarPose.Solve(input, right, left, 0.5f, 0.2f, 0.6f);
            Check(!r.Clamped && Near(r.EyeDistance, 0.2f - 0.07f), "pose: scale shrinks eye offsets and range");

            // garbage in, no NaN out
            input.AnkleR = input.AnkleL = Vector3.Zero;
            input.ShinR = new Vector3(float.NaN, 0, 0); input.ShinL = Vector3.Zero;
            r = BarPose.Solve(input, right, left, 1f, 0.2f, 0.3f);
            Check(Finite(r.BarPositionR) && Finite(r.BarPositionL) && Finite(r.BarRotation) && Finite(r.CuffRotationR) && Finite(r.CuffRotationL), "pose: coincident ankles / NaN shin stay finite");
        }

        private static void LockTests()
        {
            var m = new LockMachine { CloseSeconds = 0.5f, OpenSeconds = 0.5f, AttemptSeconds = 1f };
            Check(!m.Worn && m.RequestLock() && m.State == LockState.Closing && m.Worn, "lock: ring choice starts closing");
            Check(!m.RequestLock(), "lock: choosing again while worn does nothing");
            m.Tick(0.3f, false, 0);
            var e = m.Tick(0.3f, false, 0);
            Check(e == LockMachine.Event.Locked && m.State == LockState.Locked && m.Closed == 1f, "lock: closed after CloseSeconds");
            Check(Near(m.Chance, 0.1f), "lock: chance starts at the handcuff base chance");

            for (int i = 0; i < 4; i++) m.Tick(0.2f, true, 0.99f);
            Check(m.Attempt > 0.7f && m.Attempt < 1f, "lock: holding the key fills the attempt");
            m.Tick(0.1f, false, 0.99f);
            Check(m.Attempt == 0f, "lock: releasing early cancels the attempt");

            for (int i = 0; i < 5; i++) e = m.Tick(0.2f, true, 0.99f);
            Check(e == LockMachine.Event.AttemptFailed && m.Failures == 1 && Near(m.Chance, 0.12f), "lock: failed attempt raises the chance by 0.02");
            e = m.Tick(2f, true, 0f);
            Check(e == LockMachine.Event.None && m.State == LockState.Locked, "lock: holding on after an attempt does not start another");
            m.Tick(0.1f, false, 0);
            for (int i = 0; i < 5; i++) e = m.Tick(0.2f, true, 0.05f);
            Check(e == LockMachine.Event.Unlocking && m.State == LockState.Opening, "lock: lucky roll opens the cuffs");
            m.Tick(0.3f, false, 0);
            e = m.Tick(0.3f, false, 0);
            Check(e == LockMachine.Event.Unlocked && !m.Worn && m.Closed == 0f, "lock: off after OpenSeconds");

            m.RequestLock();
            m.Tick(0.1f, true, 0);
            m.Tick(1f, true, 0);
            e = m.Tick(5f, true, 0f);
            Check(m.State == LockState.Locked && e == LockMachine.Event.None, "lock: a key held since before locking does not unlock");

            m.Restore(true, float.NaN);
            Check(m.State == LockState.Locked && Near(m.Chance, 0.1f) && m.Closed == 1f, "restore: locked, NaN chance -> base");
            m.Restore(true, 0.5f);
            Check(Near(m.Chance, 0.5f), "restore: saved chance kept");
            m.Reset();
            Check(!m.Worn && m.Closed == 0f, "reset: unlocked");
            m.Restore(true, 0.2f);
            e = m.Tick(float.NaN, true, 0f);
            Check(m.Attempt == 0f && e == LockMachine.Event.None, "lock: NaN time is ignored");

            // The mod's default: one full attempt always frees (SuccessChance 1), even with the highest roll.
            var one = new LockMachine { BaseChance = 1f, AttemptSeconds = 1f, CloseSeconds = 0.5f };
            one.RequestLock();
            one.Tick(0.5f, false, 0f);
            e = one.Tick(1f, true, 0.9999f);
            Check(e == LockMachine.Event.Unlocking, "lock: chance 1 unlocks on the first attempt");

            // Raised live while locked (a saved lower chance does not hold it back).
            var live = new LockMachine { AttemptSeconds = 1f };
            live.Restore(true, 0.1f);
            live.Tick(0.1f, false, 0f);
            live.BaseChance = 1f;
            e = live.Tick(1f, true, 0.9f);
            Check(e == LockMachine.Event.Unlocking, "lock: a raised base chance applies to the running lock");
        }

        private static Leg MakeLeg(float side, float kneeForward = 0.04f) => new Leg
        {
            Hip = new Vector3(0.08f * side, 0.80f, 0f),
            Knee = new Vector3(0.06f * side, 0.45f, kneeForward),
            Ankle = new Vector3(0.05f * side, 0.08f, 0f),
            CuffLift = 0.085f,
        };

        private static float Across(Leg r, LegFix fr, Leg l, LegFix fl) => (LegSpread.Apply(l, fl).Cuff - LegSpread.Apply(r, fr).Cuff).X;

        private static void LegTests()
        {
            Vector3 up = Vector3.UnitY, side = Vector3.UnitX;
            Leg r = MakeLeg(-1f), l = MakeLeg(1f);
            var res = LegSpread.Solve(r, l, 0.25f, side, up);
            Leg r2 = LegSpread.Apply(r, res.Right), l2 = LegSpread.Apply(l, res.Left);
            Check(res.Before < 0.13f && Near(res.After, 0.25f, 0.002f), "legs: close ankles are spread to the bar width (" + res.Before.ToString("0.000") + " -> " + res.After.ToString("0.000") + ")");
            Check(Near(Vector3.Distance(r.Hip, r.Knee), Vector3.Distance(r2.Hip, r2.Knee)) &&
                  Near(Vector3.Distance(r.Knee, r.Ankle), Vector3.Distance(r2.Knee, r2.Ankle)), "legs: bone lengths unchanged");
            r2 = LegSpread.Apply(LegSpread.Lower(r, Vector3.UnitY, res.HipDrop), res.Right);
            l2 = LegSpread.Apply(LegSpread.Lower(l, Vector3.UnitY, res.HipDrop), res.Left);
            Check(r2.Ankle.X < r.Ankle.X - 0.04f && l2.Ankle.X > l.Ankle.X + 0.04f && r2.Knee.X < r.Knee.X && l2.Knee.X > l.Knee.X, "legs: ankles and knees move outward");
            Leg r3 = LegSpread.Apply(LegSpread.Lower(r, Vector3.UnitY, res.HipDrop), res.Right);
            Check(Math.Abs(r3.Ankle.Y - r.Ankle.Y) < 0.01f && res.HipDrop < 0.02f, "legs: feet stay on the floor, hips at most a little lower (" + res.HipDrop.ToString("0.000") + " m)");
            Check(Finite(res.Right.Thigh) && Finite(res.Right.Knee) && Finite(res.Left.Thigh) && Finite(res.Left.Knee), "legs: finite rotations");

            // Wide legs are pulled in to the bar width too (the bar is rigid).
            var wide = LegSpread.Solve(MakeLeg(-6f), MakeLeg(6f), 0.25f, side, up);
            Check(wide.Before > 0.4f && Near(wide.After, 0.25f, 0.003f), "legs: wide ankles are pulled in to the bar width");
            var exact = LegSpread.Solve(MakeLeg(-2.5f), MakeLeg(2.5f), 0.25f, side, up);
            Check(Near(exact.After, 0.25f, 0.003f), "legs: any start distance ends at the bar width");

            // Walking: one foot 0.25 m ahead -> the stride is kept up to 30 degrees, the rest becomes sideways.
            Leg wr = MakeLeg(-1f), wl = MakeLeg(1f);
            wl.Ankle += new Vector3(0, 0, 0.25f); wl.Knee += new Vector3(0, 0, 0.12f);
            var st = LegSpread.Solve(wr, wl, 0.25f, side, up);
            Vector3 wv = LegSpread.Apply(wl, st.Left).Cuff - LegSpread.Apply(wr, st.Right).Cuff;
            Check(Near(wv.Length(), 0.25f, 0.003f) && Near(wv.X, 0.25f * (float)Math.Cos(0.5236), 0.006f) && wv.Z > 0.1f,
                  "legs: walking stride limited to 30 degrees off sideways (" + wv.X.ToString("0.000") + " across, " + wv.Z.ToString("0.000") + " ahead)");

            // Constrain: crossed legs open to the side; a vector inside the cone only gets the bar's length.
            var crossed = LegSpread.Constrain(new Vector3(-0.05f, 0, 0.1f), side, 0.25f, 0.5236f);
            Check(crossed.X > 0.2f && Near(crossed.Length(), 0.25f), "legs: crossed legs are opened to the side");
            var inside = LegSpread.Constrain(new Vector3(0.1f, 0, 0.02f), side, 0.3f, 0.5236f);
            Check(Near(Vector3.Normalize(inside), Vector3.Normalize(new Vector3(0.1f, 0, 0.02f))) && Near(inside.Length(), 0.3f), "legs: direction inside the cone is kept");
            var zero = LegSpread.Constrain(Vector3.Zero, side, 0.25f, 0.5236f);
            Check(Near(zero, new Vector3(0.25f, 0, 0)), "legs: ankles on top of each other use the sideways axis");

            // Very wide bar with straight legs: the hips come down instead of the feet lifting, bones keep their lengths.
            var straightR = new Leg { Hip = new Vector3(-0.08f, 0.8f, 0), Knee = new Vector3(-0.08f, 0.44f, 0.01f), Ankle = new Vector3(-0.08f, 0.08f, 0), CuffLift = 0.085f };
            var straightL = new Leg { Hip = new Vector3(0.08f, 0.8f, 0), Knee = new Vector3(0.08f, 0.44f, 0.01f), Ankle = new Vector3(0.08f, 0.08f, 0), CuffLift = 0.085f };
            var big = LegSpread.Solve(straightR, straightL, 0.7f, side, up);
            Leg br = LegSpread.Apply(LegSpread.Lower(straightR, up, big.HipDrop), big.Right);
            Check(big.HipDrop > 0.01f && big.HipDrop < 0.3f, "legs: a wide bar lowers the hips (" + big.HipDrop.ToString("0.000") + " m)");
            Check(Near(big.After, 0.7f, 0.005f) && Math.Abs(br.Ankle.Y - 0.08f) < 0.03f, "legs: wide bar reached with the feet near the floor (" + br.Ankle.Y.ToString("0.000") + ")");
            Check(Near(Vector3.Distance(br.Hip, br.Knee), 0.36f, 0.002f), "legs: no stretched bones with a wide bar");

            // Crawling: shins roughly horizontal, knees on the floor, feet behind.
            Leg cr = new Leg { Hip = new Vector3(-0.08f, 0.45f, 0f), Knee = new Vector3(-0.07f, 0.08f, 0.05f), Ankle = new Vector3(-0.05f, 0.1f, -0.33f), CuffLift = 0.085f };
            Leg cl = new Leg { Hip = new Vector3(0.08f, 0.45f, 0f), Knee = new Vector3(0.07f, 0.08f, 0.05f), Ankle = new Vector3(0.05f, 0.1f, -0.33f), CuffLift = 0.085f };
            var c = LegSpread.Solve(cr, cl, 0.3f, side, up);
            Check(Near(c.After, 0.3f, 0.004f) && c.HipDrop == 0f, "legs: crawling ankles spread to the bar width (" + c.Before.ToString("0.000") + " -> " + c.After.ToString("0.000") + ")");
        }

        private static void RefitTests(string path)
        {
            var m = Load(path);
            m.ToUnity();
            var original = BarFit.SaveTubes(m);
            float full = BarFit.FullLengthMax(m, original);
            foreach (float hole in new[] { 0.215f, 0.33f, 0.6f, full - 0.05f })
            {
                var (min, max) = BarFit.FitFor(m, original, hole);
                Check(hole >= min && hole <= max, "refit: hole distance " + hole.ToString("0.00") + " inside the telescope (" + min.ToString("0.000") + "-" + max.ToString("0.000") + ")");
            }
            BarFit.FitFor(m, original, 0.6f);
            var (min2, max2) = BarFit.FitFor(m, original, 0.25f);
            Check(0.25f >= min2 && 0.25f <= max2, "refit: shrinking after widening starts from the original tubes");
            Check(full > 0.6f, "refit: the full-length bar reaches " + full.ToString("0.00") + " model m");
        }
        /// <summary>Points on an elliptic tube (leg) along y around (cx, cz), radii rx, rz, from y0 to y1.</summary>
        private static List<Vector3> Tube(float cx, float cz, float rx, float rz, float y0, float y1)
        {
            var pts = new List<Vector3>();
            for (float y = y0; y <= y1 + 1e-6f; y += 0.004f)
                for (int k = 0; k < 48; k++)
                {
                    double a = k * Math.PI * 2 / 48;
                    pts.Add(new Vector3(cx + rx * (float)Math.Cos(a), y, cz + rz * (float)Math.Sin(a)));
                }
            return pts;
        }

        private static void GirthTests()
        {
            // Round leg 4 cm radius, off the bone axis by 1 cm: centre found, radius 4 cm.
            var leg = Tube(0.01f, 0f, 0.04f, 0.04f, 0f, 0.3f);
            var fit = LimbGirth.Measure(leg, new Vector3(0, 0.15f, 0), Vector3.UnitY, 0.012f, 0.09f);
            Check(fit.Ok && Near(fit.Radius, 0.04f, 0.001f) && Near(fit.Center, new Vector3(0.01f, 0.15f, 0), 0.002f),
                  "girth: round leg radius and centre (" + fit.Radius.ToString("0.0000") + ")");

            // Oval (boot) 5 x 3.5 cm: the cuff must clear the long side.
            var oval = LimbGirth.Measure(Tube(0, 0, 0.05f, 0.035f, 0f, 0.3f), new Vector3(0, 0.15f, 0), Vector3.UnitY, 0.012f, 0.09f);
            Check(oval.Ok && Near(oval.Radius, 0.05f, 0.001f), "girth: oval leg uses its longest radius");

            // The other leg 20 cm away and a floor far below are ignored.
            var both = Tube(0f, 0f, 0.04f, 0.04f, 0f, 0.3f);
            both.AddRange(Tube(0.2f, 0f, 0.04f, 0.04f, 0f, 0.3f));
            var one = LimbGirth.Measure(both, new Vector3(0, 0.15f, 0), Vector3.UnitY, 0.012f, 0.09f);
            Check(one.Ok && Near(one.Radius, 0.04f, 0.001f), "girth: the other leg is not counted");

            // A tilted shin: slice across the shin, not horizontal.
            var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
            var tilted = Tube(0, 0, 0.04f, 0.04f, -0.15f, 0.15f).ConvertAll(v => Vector3.Transform(v, tilt));
            var t = LimbGirth.Measure(tilted, Vector3.Zero, Vector3.Transform(Vector3.UnitY, tilt), 0.012f, 0.09f);
            Check(t.Ok && Near(t.Radius, 0.04f, 0.0015f), "girth: tilted shin is sliced across it");

            // Sparse mesh (rings 3 cm apart, 12 sides): cutting the triangles still gives the outline at any height.
            var verts = new List<Vector3>(); var tris = new List<int>();
            for (int ring = 0; ring < 5; ring++)
                for (int k = 0; k < 12; k++)
                {
                    double a = k * Math.PI * 2 / 12;
                    verts.Add(new Vector3(0.04f * (float)Math.Cos(a), ring * 0.03f, 0.04f * (float)Math.Sin(a)));
                }
            for (int ring = 0; ring < 4; ring++)
                for (int k = 0; k < 12; k++)
                {
                    int a0 = ring * 12 + k, a1 = ring * 12 + (k + 1) % 12, b0 = a0 + 12, b1 = a1 + 12;
                    tris.AddRange(new[] { a0, b0, a1, a1, b0, b1 });
                }
            var cut = new List<Vector3>();
            LimbGirth.Slice(verts, tris, new Vector3(0, 0.055f, 0), Vector3.UnitY, 0.15f, cut);
            var sliced = LimbGirth.Measure(cut, new Vector3(0, 0.055f, 0), Vector3.UnitY, 0.002f, 0.09f);
            Check(sliced.Ok && sliced.Radius > 0.038f && sliced.Radius <= 0.0401f && cut.TrueForAll(q => Math.Abs(q.Y - 0.055f) < 1e-5f),
                  "girth: triangles cut between vertex rings give the outline (" + cut.Count + " pts, r " + sliced.Radius.ToString("0.0000") + ")");

            // Oval leg 3.0 (towards the bar) x 2.4 cm, centred 1 cm off: extents in the cuff's frame, oval scale.
            var ovalRing = Tube(0.01f, 0f, 0.030f, 0.024f, 0.149f, 0.151f);
            var (oc, hx, hz) = LimbGirth.Extents(ovalRing, new Vector3(0, 0.15f, 0), Vector3.UnitY, new Vector3(1, 0.3f, 0));
            Check(Near(hx, 0.030f, 0.0005f) && Near(hz, 0.024f, 0.0005f) && Near(oc, new Vector3(0.01f, 0.15f, 0), 0.002f),
                  "girth: oval extents in the cuff frame (" + hx.ToString("0.0000") + " x " + hz.ToString("0.0000") + ")");
            var (_, hx90, hz90) = LimbGirth.Extents(ovalRing, new Vector3(0, 0.15f, 0), Vector3.UnitY, Vector3.UnitZ);
            Check(Near(hx90, 0.024f, 0.0005f) && Near(hz90, 0.030f, 0.0005f), "girth: extents follow the bar direction");
            var os = LimbGirth.OvalScale(0.030f, 0.024f, 0.004f, 0.0334f, 0.3f, 2.5f, 1.3f);
            Check(Near(os.X, 0.034f / 0.0334f) && Near(os.Z, 0.028f / 0.0334f) && Near(os.Y, (os.X + os.Z) / 2f), "girth: oval scale per axis");
            var capped = LimbGirth.OvalScale(0.040f, 0.020f, 0f, 0.04f, 0.3f, 2.5f, 1.3f);
            Check(Near(capped.X, 1f) && Near(capped.Z, 1f / 1.3f), "girth: oval limited to MaxOval (narrow side widened)");

            // Extra geometry 6-8 cm off the leg in the same slice (the foot): ignored, centre and radius unchanged.
            var withFoot = Tube(0.01f, 0f, 0.03f, 0.03f, 0.14f, 0.16f);
            for (int k = 0; k < 60; k++) withFoot.Add(new Vector3(0.01f + 0.06f + k * 0.0003f, 0.15f, 0.02f));
            var wf = LimbGirth.Measure(withFoot, new Vector3(0, 0.15f, 0), Vector3.UnitY, 0.012f, 0.09f);
            Check(wf.Ok && Near(wf.Radius, 0.03f, 0.001f) && Near(wf.Center, new Vector3(0.01f, 0.15f, 0), 0.002f) && wf.Outline.Count < withFoot.Count,
                  "girth: other geometry in the slice (foot) is dropped (r " + wf.Radius.ToString("0.0000") + ")");

            var none = LimbGirth.Measure(new List<Vector3>(), Vector3.Zero, Vector3.UnitY, 0.012f, 0.09f);
            Check(!none.Ok, "girth: no vertices -> no fit (cuff keeps its scale)");

            Check(Near(LimbGirth.CuffScale(0.04f, 0.004f, 0.033f, 0.3f, 2.5f), 0.044f / 0.033f) &&
                  Near(LimbGirth.CuffScale(1f, 0f, 0.033f, 0.3f, 2.5f), 2.5f) && Near(LimbGirth.CuffScale(0.04f, 0f, 0f, 0.3f, 2.5f), 1f),
                  "girth: cuff scale = (leg + clearance) / hole, clamped");
        }

        private static void PoseCuffScaleTests()
        {
            var right = new CuffSide { Hole = new Vector3(-0.2f, 0, 0), Eye = new Vector3(-0.15f, 0, 0) };
            var left = new CuffSide { Hole = new Vector3(0.2f, 0, 0), Eye = new Vector3(0.15f, 0, 0) };
            var input = new BarPose.Input { AnkleR = new Vector3(-0.12f, 0.1f, 0), AnkleL = new Vector3(0.12f, 0.1f, 0), ShinR = Vector3.UnitY, ShinL = Vector3.UnitY, Up = Vector3.UnitY, Forward = Vector3.UnitZ };
            var p = BarPose.Solve(input, right, left, 1.2f, 0.9f, 0.75f, 0.1f, 1f);
            // The cuff eye (cuff scale) and the bar's eye (bar scale) are the same world point on each side.
            Vector3 cuffEyeR = p.CuffPositionR + Vector3.Transform((right.Eye - right.Hole) * 1.2f, p.CuffRotationR);
            Vector3 barEyeR = p.BarPositionR + Vector3.Transform((right.Eye - right.Hole) * 0.75f, p.BarRotation);
            Vector3 cuffEyeL = p.CuffPositionL + Vector3.Transform((left.Eye - left.Hole) * 0.9f, p.CuffRotationL);
            Vector3 barEyeL = p.BarPositionL + Vector3.Transform((left.Eye - left.Hole) * 0.75f, p.BarRotation);
            Check(Near(cuffEyeR, barEyeR, 1e-4f) && Near(cuffEyeL, barEyeL, 1e-4f) && !p.Clamped, "pose: bar eyes meet differently sized cuffs");
        }
        private static void SlowdownTests()
        {
            Check(Near(WalkSlowdown.Factor(16f, 16f, 60f, 0.85f, 0.5f), 0.85f) && Near(WalkSlowdown.Factor(60f, 16f, 60f, 0.85f, 0.5f), 0.5f) &&
                  Near(WalkSlowdown.Factor(38f, 16f, 60f, 0.85f, 0.5f), 0.675f), "slowdown: narrowest / widest / middle width");
            Check(Near(WalkSlowdown.Factor(10f, 16f, 60f, 0.85f, 0.5f), 0.85f) && Near(WalkSlowdown.Factor(80f, 16f, 60f, 0.85f, 0.5f), 0.5f),
                  "slowdown: widths outside the range use its ends");
            Check(Near(WalkSlowdown.Factor(30f, 30f, 30f, 0.8f, 0.5f), 0.8f) && Near(WalkSlowdown.Factor(60f, 16f, 60f, 1.5f, -1f), 0f) &&
                  Near(WalkSlowdown.Factor(16f, 16f, 60f, 1.5f, 0.5f), 1f), "slowdown: empty range, factor kept within 0..1");

            var v = new ScaledValue();
            float w = v.Next(1.1f, 0.5f);
            Check(Near(w, 0.55f) && Near(v.Next(w, 0.5f), 0.55f) && Near(v.Next(0.55f, 0.4f), 0.44f),
                  "scaled value: its own write is not taken as the game's (no compounding), a new factor applies to the base");
            Check(Near(v.Next(0.65f, 0.5f), 0.325f) && Near(v.Base, 0.65f), "scaled value: a value the game writes becomes the base");
            Check(Near(v.Release(0.325f), 0.65f) && !v.Active, "scaled value: released, the game's value goes back");
            v.Next(1f, 0.5f);
            Check(Near(v.Release(0.9f), 0.9f), "scaled value: released after the game wrote a new value, that one stays");
        }
    }
}
