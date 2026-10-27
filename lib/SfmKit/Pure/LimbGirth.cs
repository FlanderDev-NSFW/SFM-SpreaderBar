using System;
using System.Collections.Generic;
using System.Numerics;

namespace SfmKit.Pure
{
    /// <summary>
    /// How thick a limb (leg with socks, boots ..., arm, neck) is where a cuff sits, from the skinned mesh vertices (pure maths,
    /// no Unity). Moved here from SpreaderBar (its cuff fit, L3 2026-10-12) for WallRestraint's tubes, cuffs and collar.
    /// Takes the vertices in a thin slice across the leg (perpendicular to <c>axis</c>, centred on <c>center</c>), finds the
    /// slice's middle and the largest distance from it, so a round cuff hole around that middle clears the whole outline.
    /// </summary>
    internal static class LimbGirth
    {
        public struct Fit
        {
            public bool Ok;
            public Vector3 Center;  // middle of the leg's cross-section (world)
            public float Radius;    // largest distance of the outline from Center, across the leg
            public int Points;      // vertices used
            public List<Vector3> Outline; // the points used (world)
        }

        /// <param name="points">Vertex positions (world).</param>
        /// <param name="center">A point inside the leg at cuff height (the bone axis).</param>
        /// <param name="axis">Along the shin.</param>
        /// <param name="halfSlab">Half the slice thickness along the shin (a cuff ring is about 1.5-2 cm tall).</param>
        /// <param name="maxRadius">Ignore vertices further than this from the axis (the other leg, the floor, a skirt).</param>
        public static Fit Measure(IEnumerable<Vector3> points, Vector3 center, Vector3 axis, float halfSlab, float maxRadius, int minPoints = 12)
        {
            axis = Unit(axis, Vector3.UnitY);
            var slice = new List<Vector3>();
            foreach (var p in points)
            {
                Vector3 d = p - center;
                float along = Vector3.Dot(d, axis);
                if (Math.Abs(along) > halfSlab) continue;
                Vector3 across = d - along * axis;
                if (across.LengthSquared() > maxRadius * maxRadius) continue;
                slice.Add(across);
            }
            if (slice.Count < minPoints) return new Fit { Ok = false, Center = center, Points = slice.Count };

            // Middle of the outline: the middle of its box (vertex means lean to where the mesh is detailed). Start from the
            // median point: other geometry in the slice (the foot) cannot drag it like the box.
            Vector3 u = Unit(Vector3.Cross(axis, Math.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ), Vector3.UnitX);
            Vector3 v = Vector3.Cross(axis, u);
            var us = new float[slice.Count];
            var vs = new float[slice.Count];
            for (int i = 0; i < slice.Count; i++) { us[i] = Vector3.Dot(slice[i], u); vs[i] = Vector3.Dot(slice[i], v); }
            Array.Sort(us);
            Array.Sort(vs);
            Vector3 mid = u * us[us.Length / 2] + v * vs[vs.Length / 2];

            // The cut can also catch other parts near the leg (the foot, a strap): keep the loop around the leg - points
            // within 1.5x the median distance from the middle - and recentre, twice.
            for (int pass = 0; pass < 2; pass++)
            {
                var dist = new float[slice.Count];
                for (int i = 0; i < dist.Length; i++) dist[i] = Vector3.Distance(slice[i], mid);
                var sorted = (float[])dist.Clone();
                Array.Sort(sorted);
                float cut = sorted[sorted.Length / 2] * 1.5f;
                var kept = new List<Vector3>(slice.Count);
                for (int i = 0; i < dist.Length; i++) if (dist[i] <= cut) kept.Add(slice[i]);
                if (kept.Count >= minPoints) slice = kept;
                mid = BoxMiddle(slice, u, v);
            }

            float radius = 0f;
            var outline = new List<Vector3>(slice.Count);
            foreach (var a in slice)
            {
                radius = Math.Max(radius, Vector3.Distance(a, mid));
                outline.Add(center + a);
            }
            return new Fit { Ok = true, Center = center + mid, Radius = radius, Points = slice.Count, Outline = outline };
        }

        private static Vector3 BoxMiddle(List<Vector3> pts, Vector3 u, Vector3 v)
        {
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            foreach (var a in pts)
            {
                float pu = Vector3.Dot(a, u), pv = Vector3.Dot(a, v);
                minU = Math.Min(minU, pu); maxU = Math.Max(maxU, pu);
                minV = Math.Min(minV, pv); maxV = Math.Max(maxV, pv);
            }
            return u * ((minU + maxU) * 0.5f) + v * ((minV + maxV) * 0.5f);
        }

        /// <summary>
        /// The outline of a mesh cut by the plane through <paramref name="center"/> across <paramref name="axis"/>: every
        /// triangle edge that crosses the plane gives one point. Exact whatever the mesh density (vertex rings on a leg are
        /// a few cm apart). Only triangles with a corner within <paramref name="near"/> of the centre are cut. Points are added
        /// to <paramref name="into"/>.
        /// </summary>
        public static void Slice(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> triangles, Vector3 center, Vector3 axis, float near, List<Vector3> into)
        {
            axis = Unit(axis, Vector3.UnitY);
            float near2 = near * near;
            for (int t = 0; t + 2 < triangles.Count; t += 3)
            {
                int ia = triangles[t], ib = triangles[t + 1], ic = triangles[t + 2];
                if ((uint)ia >= vertices.Count || (uint)ib >= vertices.Count || (uint)ic >= vertices.Count) continue;
                Vector3 a = vertices[ia], b = vertices[ib], c = vertices[ic];
                if (float.IsNaN(a.X) || float.IsNaN(b.X) || float.IsNaN(c.X)) continue; // a corner not on this bone
                if ((a - center).LengthSquared() > near2 && (b - center).LengthSquared() > near2 && (c - center).LengthSquared() > near2) continue;
                float da = Vector3.Dot(a - center, axis), db = Vector3.Dot(b - center, axis), dc = Vector3.Dot(c - center, axis);
                Cut(a, b, da, db, into);
                Cut(b, c, db, dc, into);
                Cut(c, a, dc, da, into);
            }
        }

        private static void Cut(Vector3 a, Vector3 b, float da, float db, List<Vector3> into)
        {
            if ((da > 0f) == (db > 0f) || da == db) return;
            into.Add(a + (b - a) * (da / (da - db)));
        }

        /// <summary>
        /// The leg's outline seen in a cuff's frame: across the shin, along <paramref name="xHint"/> (towards the bar) and
        /// the direction perpendicular to both. Returns the middle of the outline's box in that frame and its half-widths
        /// (<c>hx</c> along x, <c>hz</c> along z).
        /// </summary>
        public static (Vector3 Center, float Hx, float Hz) Extents(IReadOnlyList<Vector3> outline, Vector3 around, Vector3 axis, Vector3 xHint)
        {
            axis = Unit(axis, Vector3.UnitY);
            Vector3 x = xHint - Vector3.Dot(xHint, axis) * axis;
            x = x.LengthSquared() > 1e-10f ? Vector3.Normalize(x) : Unit(Vector3.Cross(axis, Vector3.UnitZ), Vector3.UnitX);
            Vector3 z = Vector3.Cross(x, axis);
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue, along = 0f;
            foreach (var p in outline)
            {
                Vector3 d = p - around;
                float px = Vector3.Dot(d, x), pz = Vector3.Dot(d, z);
                minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
                minZ = Math.Min(minZ, pz); maxZ = Math.Max(maxZ, pz);
                along += Vector3.Dot(d, axis);
            }
            if (outline.Count == 0) return (around, 0f, 0f);
            Vector3 center = around + x * ((minX + maxX) * 0.5f) + z * ((minZ + maxZ) * 0.5f) + axis * (along / outline.Count);
            return (center, (maxX - minX) * 0.5f, (maxZ - minZ) * 0.5f);
        }

        /// <summary>Per-axis cuff scale (model x = towards the bar, y = along the shin, z = across) so the hole clears an
        /// oval leg with half-widths <paramref name="hx"/>, <paramref name="hz"/> by <paramref name="clearance"/>. The oval is
        /// limited to <paramref name="maxAspect"/> (the narrow side then stays a little loose); y follows the mean.</summary>
        public static Vector3 OvalScale(float hx, float hz, float clearance, float holeRadius, float min, float max, float maxAspect)
        {
            float sx = CuffScale(hx, clearance, holeRadius, min, max), sz = CuffScale(hz, clearance, holeRadius, min, max);
            if (maxAspect < 1f) maxAspect = 1f;
            if (sx > sz * maxAspect) sz = sx / maxAspect;
            else if (sz > sx * maxAspect) sx = sz / maxAspect;
            return new Vector3(sx, (sx + sz) * 0.5f, sz);
        }

        /// <summary>Cuff scale that makes the hole (<paramref name="holeRadius"/>, model units) <paramref name="clearance"/>
        /// wider than the leg, clamped to [<paramref name="min"/>, <paramref name="max"/>].</summary>
        public static float CuffScale(float legRadius, float clearance, float holeRadius, float min, float max)
        {
            if (!(holeRadius > 1e-5f) || !(legRadius > 0f)) return Math.Clamp(1f, min, max);
            return Math.Clamp((legRadius + clearance) / holeRadius, min, max);
        }

        private static Vector3 Unit(Vector3 v, Vector3 fallback)
        {
            float l = v.Length();
            if (!(l > 1e-6f) || float.IsInfinity(l)) return fallback;
            return v / l;
        }
    }
}
