"""Export the spreader bar .blend into the mod's runtime model format (assets/spreaderbar.sbm).

Usage (Blender 4.1+, headless):
    blender -b <Spreader_bar.blend> --python mods/SpreaderBar/tools/export_model.py -- <out.sbm>

What it does:
  * enables the rig's bevel / subdivision modifiers (the file toggles them off through drivers),
  * evaluates the meshes in the rest pose (bar fully extended, cuffs closed),
  * finds out which vertices move with what by posing the rig and diffing, instead of trusting
    vertex-group names (the left cuff is weighted to the '.R' bones in the source file),
  * writes rigid parts (bar half, chain, cuff, hinge per side) in Blender coordinates relative to
    nothing (absolute rest positions); the runtime does the axis conversion (Logic/ModelFormat.cs).

Format (little endian): "SBAR" u32 version=1
  f32 slideRange
  2 x side record (0 = R = -x, 1 = L = +x): f32x3 holeCenter, f32 holeRadius, f32x3 cuffPivot,
      f32x3 hingePivot, f32x3 hingeAxis, f32 hingeOpenAngle
  u32 partCount, then per part: u8 side, u8 kind (0 bar, 1 chain, 2 cuff, 3 hinge), f32x3 color,
      u32 vertexCount, f32x3 positions[], f32x3 normals[], u32 indexCount, u32 indices[]
"""
import math
import struct
import sys

import bpy
from mathutils import Vector

KIND_BAR, KIND_CHAIN, KIND_CUFF, KIND_HINGE = 0, 1, 2, 3
SLIDE_RANGE = 0.35  # 'Bar Slide' Limit Location: local y in [-0.35, 0]

out_path = sys.argv[sys.argv.index("--") + 1] if "--" in sys.argv else "spreaderbar.sbm"
arm = bpy.data.objects["Armature"]
root = arm.pose.bones["Root"]
meshes = [o for o in bpy.data.objects if o.type == "MESH" and o.name.endswith(".Geo")]

# --- render-quality modifiers, no drivers -------------------------------------------------------
for o in meshes:
    if o.animation_data:
        for fc in list(o.animation_data.drivers):
            if fc.data_path.startswith("modifiers"):
                o.animation_data.drivers.remove(fc)
    for m in o.modifiers:
        if m.type == "SUBSURF":
            m.levels = int(root.get("subd_render_level", 1))
            m.show_viewport = True
        elif m.type == "BEVEL":
            m.segments = int(root.get("bevel_segments", 4))
            m.width_pct = float(root.get("bevel_width", 19))
            m.show_viewport = True


def reset_pose():
    for pb in arm.pose.bones:
        pb.location = (0, 0, 0)
        pb.rotation_quaternion = (1, 0, 0, 0)
        pb.rotation_euler = (0, 0, 0)


def evaluate():
    """World-space vertex positions per object (vertex order is stable between poses)."""
    dg = bpy.context.evaluated_depsgraph_get()
    dg.update()
    result = {}
    for o in meshes:
        e = o.evaluated_get(dg)
        m = e.to_mesh()
        result[o.name] = [e.matrix_world @ v.co for v in m.vertices]
        e.to_mesh_clear()
    return result


def moved(a, b):
    return {name: [(p - q).length > 1e-6 for p, q in zip(a[name], b[name])] for name in a}


reset_pose()
rest = evaluate()
arm.pose.bones["Bar Slide"].location = (0, -0.2, 0)
slide_moved = moved(rest, evaluate())
reset_pose()
arm.pose.bones["CuffSlider.L"].location = (1, 0, 0)
arm.pose.bones["CuffSlider.R"].location = (-1, 0, 0)
open_pos = evaluate()
hinge_moved = moved(rest, open_pos)
reset_pose()
evaluate()

# --- geometry with split normals, per rigid part --------------------------------------------------
dg = bpy.context.evaluated_depsgraph_get()
parts = {}  # (side, kind) -> [positions, normals, indices, index_of]
colors = {
    KIND_BAR: None,  # per side below
    KIND_CHAIN: tuple(root["col_chain"]),
    KIND_CUFF: tuple(root["col_shackle"]),
    KIND_HINGE: tuple(root["col_shackle"]),
}
bar_colors = {}


def part(side, kind):
    key = (side, kind)
    if key not in parts:
        parts[key] = ([], [], [], {})
    return parts[key]


for o in meshes:
    e = o.evaluated_get(dg)
    m = e.to_mesh()
    m.calc_loop_triangles()
    normals = m.corner_normals
    mw = e.matrix_world
    nmat = mw.to_3x3().inverted().transposed()
    slid = slide_moved[o.name]
    hinge = hinge_moved[o.name]
    for tri in m.loop_triangles:
        vs = tri.vertices
        x = sum((mw @ m.vertices[v].co for v in vs), Vector()) / 3
        if o.name.startswith("Bar"):
            side = 1 if sum(slid[v] for v in vs) >= 2 else 0  # the half 'Bar Slide' moves = L half
            kind = KIND_BAR
        else:
            side = 1 if x.x > 0 else 0  # by position: vertex-group names are not reliable here
            if o.name.startswith("chain"):
                kind = KIND_CHAIN
            else:
                kind = KIND_HINGE if sum(hinge[v] for v in vs) >= 2 else KIND_CUFF
        pos, nor, idx, index_of = part(side, kind)
        corners = []
        for li, v in zip(tri.loops, vs):
            n = (nmat @ normals[li].vector).normalized()
            key = (v, round(n.x, 4), round(n.y, 4), round(n.z, 4))
            if key not in index_of:
                index_of[key] = len(pos)
                pos.append(mw @ m.vertices[v].co)
                nor.append(n)
            corners.append(index_of[key])
        idx.extend(corners)
    e.to_mesh_clear()

bar_colors[0] = tuple(root["col_bar_outer"])
bar_colors[1] = tuple(root["col_bar_inner"])

# --- per side: hole, pivots -------------------------------------------------------------------
def hole(jaw):
    """The ankle hole: circle fit (XY, least squares) to the jaw, which is a pure half ring; the inner radius is the
    nearest jaw vertex. (A 'largest empty circle' over the whole cuff finds empty space outside it instead.)"""
    a = np.array([[p.x, p.y, 1.0] for p in jaw])
    b = np.array([p.x * p.x + p.y * p.y for p in jaw])
    (cx2, cy2, c), *_ = np.linalg.lstsq(a, b, rcond=None)
    cx, cy = cx2 / 2, cy2 / 2
    inner = min(math.hypot(p.x - cx, p.y - cy) for p in jaw)
    zs = [p.z for p in jaw]
    return Vector((cx, cy, (min(zs) + max(zs)) / 2)), inner


import numpy as np
from mathutils import Matrix


def fit_hinge(pairs):
    """Rigid rotation rest -> open (Kabsch), as axis, angle and a pivot on the axis."""
    a = np.array([[p.x, p.y, p.z] for p, _ in pairs])
    b = np.array([[q.x, q.y, q.z] for _, q in pairs])
    ca, cb = a.mean(0), b.mean(0)
    u, _, vt = np.linalg.svd((a - ca).T @ (b - cb))
    d = np.sign(np.linalg.det(vt.T @ u.T))
    r = vt.T @ np.diag([1, 1, d]) @ u.T
    t = cb - r @ ca
    rot = Matrix([list(row) for row in r])
    axis, angle = rot.to_quaternion().to_axis_angle()
    # pivot: least-squares point with (I - R) p = t, nearest to the rest centroid
    p, *_ = np.linalg.lstsq(np.eye(3) - r, t, rcond=None)
    pivot = Vector(p)
    pivot = pivot + axis * axis.dot(Vector(ca) - pivot)
    return axis.normalized(), angle, pivot


def hinge_pairs(s, only_full=False):
    pairs = []
    for o in meshes:
        if o.name.startswith("Cuff Hinge" if only_full else "Cuff"):
            for p, q, mv in zip(rest[o.name], open_pos[o.name], hinge_moved[o.name]):
                if mv and (p.x > 0) == (s == 1):
                    pairs.append((p, q))
    return pairs


# The R cuff is weighted to the R bones; the L cuff is weighted to the R bones too (source-file quirk), so
# Blender's open pose swings the L hinge around the R pivot. Fit the R hinge (hinge objects only: fully weighted,
# rigid) and mirror it across x = 0 for the L cuff.
pairs_r = hinge_pairs(0, only_full=True)
axis_r, angle_r, pivot_r = fit_hinge(pairs_r)
rot = Matrix.Rotation(angle_r, 3, axis_r)
worst = max((rot @ (p - pivot_r) + pivot_r - q).length for p, q in pairs_r)
print("SBAR-CHECK R hinge rebuild max error %.6f m over %d vertices" % (worst, len(pairs_r)))
if worst > 1e-3:
    raise SystemExit("the R hinge does not move rigidly (error %.4f m)" % worst)

sides = []
for s, name in ((0, "R"), (1, "L")):
    center, radius = hole([p for p, _ in hinge_pairs(s, only_full=True)])
    cuff_bone = arm.data.bones["Cuff Main." + name]
    if s == 0:
        pivot, axis = pivot_r, axis_r
    else:  # mirror x: points (-x, y, z); rotation axes are pseudo-vectors: (x, -y, -z)
        pivot = Vector((-pivot_r.x, pivot_r.y, pivot_r.z))
        axis = Vector((axis_r.x, -axis_r.y, -axis_r.z))
    sides.append(dict(hole=center, radius=radius, cuff_pivot=arm.matrix_world @ cuff_bone.head_local,
                      hinge_pivot=pivot, hinge_axis=axis, hinge_angle=angle_r))

# --- write --------------------------------------------------------------------------------------
def v3(f, v):
    f.write(struct.pack("<3f", v[0], v[1], v[2]))


with open(out_path, "wb") as f:
    f.write(b"SBAR")
    f.write(struct.pack("<I", 1))
    f.write(struct.pack("<f", SLIDE_RANGE))
    for sd in sides:
        v3(f, sd["hole"])
        f.write(struct.pack("<f", sd["radius"]))
        v3(f, sd["cuff_pivot"])
        v3(f, sd["hinge_pivot"])
        v3(f, sd["hinge_axis"])
        f.write(struct.pack("<f", sd["hinge_angle"]))
    keys = sorted(parts)
    f.write(struct.pack("<I", len(keys)))
    total = 0
    for side, kind in keys:
        pos, nor, idx, _ = parts[(side, kind)]
        color = bar_colors[side] if kind == KIND_BAR else colors[kind]
        f.write(struct.pack("<BB", side, kind))
        v3(f, color)
        f.write(struct.pack("<I", len(pos)))
        for p in pos:
            v3(f, p)
        for n in nor:
            v3(f, n)
        f.write(struct.pack("<I", len(idx)))
        f.write(struct.pack("<%dI" % len(idx), *idx))
        total += len(pos)
        print("SBAR-PART side=%d kind=%d verts=%d tris=%d" % (side, kind, len(pos), len(idx) // 3))
    for s, sd in enumerate(sides):
        print("SBAR-SIDE %d hole=(%.4f %.4f %.4f) r=%.4f cuffPivot=(%.4f %.4f %.4f) hinge=(%.4f %.4f %.4f) axis=(%.3f %.3f %.3f) angle=%.3f" % (
            s, *sd["hole"], sd["radius"], *sd["cuff_pivot"], *sd["hinge_pivot"], *sd["hinge_axis"], sd["hinge_angle"]))
    print("SBAR-DONE %s parts=%d verts=%d" % (out_path, len(keys), total))
