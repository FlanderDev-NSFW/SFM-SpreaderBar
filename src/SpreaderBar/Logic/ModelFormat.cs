using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace SpreaderBar.Logic
{
    public enum PartKind : byte { Bar = 0, Chain = 1, Cuff = 2, Hinge = 3 }

    /// <summary>One rigid piece of the model. Positions are absolute model coordinates (metres) until <see cref="BarModel.Prepare"/>.</summary>
    public sealed class ModelPart
    {
        public int Side;               // 0 = right ankle (model -x), 1 = left ankle (model +x)
        public PartKind Kind;
        public Vector3 Color;
        public Vector3[] Positions;
        public Vector3[] Normals;
        public int[] Indices;
    }

    /// <summary>Per cuff: where the ankle goes and how the jaw opens.</summary>
    public sealed class CuffSide
    {
        public Vector3 Hole;           // centre of the ankle hole
        public float HoleRadius;       // inner radius
        public Vector3 Eye;            // where the cuff hangs in the chain (it turns about this point)
        public Vector3 HingePivot;
        public Vector3 HingeAxis;      // rotating the jaw by HingeAngle about this axis opens the cuff
        public float HingeAngle;
    }

    /// <summary>
    /// The model exported by tools/export_model.py (format described there). <see cref="Read"/> gives it in Blender
    /// coordinates (right-handed, z up); <see cref="ToUnity"/> converts to Unity's (left-handed, y up):
    /// (x, y, z) -> (x, z, y), which mirrors, so triangle winding flips and rotation angles change sign.
    /// </summary>
    public sealed class BarModel
    {
        public const uint Magic = 0x52414253; // "SBAR"
        public float SlideRange;
        public CuffSide[] Sides = new CuffSide[2];
        public List<ModelPart> Parts = new List<ModelPart>();

        public static BarModel Read(Stream stream)
        {
            using var r = new BinaryReader(stream);
            if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a spreader bar model (magic)");
            uint version = r.ReadUInt32();
            if (version != 1) throw new InvalidDataException("unsupported model version " + version);
            var m = new BarModel { SlideRange = r.ReadSingle() };
            for (int s = 0; s < 2; s++)
                m.Sides[s] = new CuffSide
                {
                    Hole = V(r), HoleRadius = r.ReadSingle(), Eye = V(r),
                    HingePivot = V(r), HingeAxis = V(r), HingeAngle = r.ReadSingle(),
                };
            uint count = r.ReadUInt32();
            for (int i = 0; i < count; i++)
            {
                var p = new ModelPart { Side = r.ReadByte(), Kind = (PartKind)r.ReadByte(), Color = V(r) };
                if (p.Side > 1 || (byte)p.Kind > 3) throw new InvalidDataException("bad part header");
                int n = checked((int)r.ReadUInt32());
                p.Positions = new Vector3[n];
                p.Normals = new Vector3[n];
                for (int k = 0; k < n; k++) p.Positions[k] = V(r);
                for (int k = 0; k < n; k++) p.Normals[k] = V(r);
                int ni = checked((int)r.ReadUInt32());
                if (ni % 3 != 0) throw new InvalidDataException("index count is not a multiple of 3");
                p.Indices = new int[ni];
                for (int k = 0; k < ni; k++)
                {
                    uint idx = r.ReadUInt32();
                    if (idx >= n) throw new InvalidDataException("index out of range");
                    p.Indices[k] = (int)idx;
                }
                m.Parts.Add(p);
            }
            return m;
        }

        private static Vector3 V(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        public static Vector3 Swap(Vector3 v) => new Vector3(v.X, v.Z, v.Y);

        /// <summary>Blender -> Unity axes, in place.</summary>
        public void ToUnity()
        {
            foreach (var side in Sides)
            {
                side.Hole = Swap(side.Hole);
                side.Eye = Swap(side.Eye);
                side.HingePivot = Swap(side.HingePivot);
                // A mirror maps a rotation about a by t to a rotation about M a by -t.
                side.HingeAxis = Swap(side.HingeAxis);
                side.HingeAngle = -side.HingeAngle;
            }
            foreach (var p in Parts)
            {
                for (int i = 0; i < p.Positions.Length; i++) p.Positions[i] = Swap(p.Positions[i]);
                for (int i = 0; i < p.Normals.Length; i++) p.Normals[i] = Swap(p.Normals[i]);
                for (int i = 0; i < p.Indices.Length; i += 3) (p.Indices[i + 1], p.Indices[i + 2]) = (p.Indices[i + 2], p.Indices[i + 1]);
            }
        }

        /// <summary>The bar tube of one side (outer tube on the right, inner tube on the left).</summary>
        public ModelPart Tube(int side) => Parts.Find(p => p.Side == side && p.Kind == PartKind.Bar);
    }
}
