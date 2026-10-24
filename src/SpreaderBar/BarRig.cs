using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using SpreaderBar.Logic;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace SpreaderBar
{
    /// <summary>
    /// The visible bar: four rigid groups (cuff R/L with their jaw, tube+chain R/L), built from the exported model and
    /// placed every frame from <see cref="BarPose"/>. Group meshes are stored relative to their own cuff's hole centre.
    /// </summary>
    internal sealed class BarRig
    {
        private readonly BarModel _model;
        private readonly float _scale;
        private GameObject _root;
        private Transform _cuffR, _cuffL, _barR, _barL, _jawR, _jawL;
        private readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>(); // materials and meshes
        private readonly List<(ModelPart Part, Mesh Mesh, NVector3 Origin)> _tubes = new List<(ModelPart, Mesh, NVector3)>();

        public BarRig(BarModel model, float scale)
        {
            _model = model;
            _scale = scale;
        }

        public bool Built => _root != null;

        public void Build(int layer)
        {
            Destroy();
            _root = new GameObject("SpreaderBar");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            Material source = FindHandcuffMaterial();
            _cuffR = Group("CuffR", layer);
            _cuffL = Group("CuffL", layer);
            _barR = Group("BarR", layer);
            _barL = Group("BarL", layer);
            _jawR = Jaw(_cuffR, 0, layer);
            _jawL = Jaw(_cuffL, 1, layer);
            foreach (var part in _model.Parts)
            {
                Transform parent = part.Kind switch
                {
                    PartKind.Cuff => part.Side == 0 ? _cuffR : _cuffL,
                    PartKind.Hinge => part.Side == 0 ? _jawR : _jawL,
                    _ => part.Side == 0 ? _barR : _barL,
                };
                CuffSide side = _model.Sides[part.Side];
                // Jaw meshes are relative to the hinge pivot (they rotate about it), everything else to the hole.
                NVector3 origin = part.Kind == PartKind.Hinge ? side.HingePivot : side.Hole;
                var mesh = AddMesh(parent, part, origin, MakeMaterial(source, part), layer);
                _owned.Add(mesh);
                if (part == _model.Tube(part.Side)) _tubes.Add((part, mesh, origin));
            }
            SetVisible(false);
        }

        private Transform Group(string name, int layer)
        {
            var go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(_root.transform, false);
            go.transform.localScale = Vector3.one * _scale;
            return go.transform;
        }

        private Transform Jaw(Transform cuff, int sideIndex, int layer)
        {
            var go = new GameObject("Jaw");
            go.layer = layer;
            go.transform.SetParent(cuff, false);
            CuffSide side = _model.Sides[sideIndex];
            go.transform.localPosition = ToUnity(side.HingePivot - side.Hole);
            return go.transform;
        }

        private static Mesh AddMesh(Transform parent, ModelPart part, NVector3 origin, Material material, int layer)
        {
            var go = new GameObject(part.Kind + (part.Side == 0 ? "R" : "L"));
            go.layer = layer;
            go.transform.SetParent(parent, false);
            var mesh = new Mesh { name = "SpreaderBar." + go.name };
            if (part.Positions.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            var v = new Vector3[part.Positions.Length];
            var n = new Vector3[part.Normals.Length];
            for (int i = 0; i < v.Length; i++) v[i] = ToUnity(part.Positions[i] - origin);
            for (int i = 0; i < n.Length; i++) n[i] = ToUnity(part.Normals[i]);
            mesh.vertices = v;
            mesh.normals = n;
            mesh.triangles = part.Indices;
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            return mesh;
        }

        /// <summary>The game's handcuff material (UnityChan toon shader), so the bar is lit and outlined like the game's own items.</summary>
        private static Material FindHandcuffMaterial()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Material>());
                foreach (var o in all)
                {
                    var mat = o.TryCast<Material>();
                    if (mat != null && mat.name != null && mat.name.StartsWith("tejyou", StringComparison.Ordinal)) return mat;
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("handcuff material lookup failed: " + e.Message); }
            Plugin.Logger.LogWarning("handcuff material not found; using a toon/standard shader");
            return null;
        }

        private Material MakeMaterial(Material source, ModelPart part)
        {
            Material mat;
            if (source != null) mat = new Material(source);
            else
            {
                Shader shader = Shader.Find("UnityChanToonShader/Toon_DoubleShadeWithFeather");
                if (shader == null) shader = Shader.Find("Standard");
                mat = new Material(shader);
            }
            mat.name = "SpreaderBar." + part.Kind;
            Texture white = Texture2D.whiteTexture;
            foreach (string tex in new[] { "_MainTex", "_BaseMap", "_1st_ShadeMap", "_2nd_ShadeMap" })
                if (mat.HasProperty(tex)) mat.SetTexture(tex, white);
            float metal = part.Kind == PartKind.Bar && part.Side == 1 ? 0.92f : 0.82f; // inner tube a bit brighter
            // The rig's colours are tints for a chrome shader; under toon shading a full tint reads as copper, so use half.
            Color baseColor = new Color((0.5f + 0.5f * part.Color.X) * metal, (0.5f + 0.5f * part.Color.Y) * metal, (0.5f + 0.5f * part.Color.Z) * metal, 1f);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseColor);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", baseColor);
            if (mat.HasProperty("_1st_ShadeColor")) mat.SetColor("_1st_ShadeColor", baseColor * 0.62f);
            if (mat.HasProperty("_2nd_ShadeColor")) mat.SetColor("_2nd_ShadeColor", baseColor * 0.38f);
            _owned.Add(mat);
            return mat;
        }

        /// <summary>Each cuff sized to its leg (the bar groups keep the model scale).</summary>
        public void SetCuffScales(NVector3 right, NVector3 left)
        {
            if (_root == null) return;
            _cuffR.localScale = ToUnity(right);
            _cuffL.localScale = ToUnity(left);
        }

        /// <summary>The tube shapes changed (bar refitted for another width): upload their vertices again.</summary>
        public void RefreshTubes()
        {
            foreach (var (part, mesh, origin) in _tubes)
            {
                if (mesh == null) continue;
                var v = new Vector3[part.Positions.Length];
                for (int i = 0; i < v.Length; i++) v[i] = ToUnity(part.Positions[i] - origin);
                mesh.vertices = v;
                mesh.RecalculateBounds();
            }
        }

        public void SetVisible(bool visible)
        {
            if (_root != null && _root.activeSelf != visible) _root.SetActive(visible);
        }

        /// <summary>Place the groups; <paramref name="closed"/> 0 = jaws open, 1 = closed.</summary>
        public void Apply(in BarPoseResult pose, float closed)
        {
            if (_root == null) return;
            Place(_cuffR, pose.CuffPositionR, pose.CuffRotationR);
            Place(_cuffL, pose.CuffPositionL, pose.CuffRotationL);
            Place(_barR, pose.BarPositionR, pose.BarRotation);
            Place(_barL, pose.BarPositionL, pose.BarRotation);
            SetJaw(_jawR, _model.Sides[0], closed);
            SetJaw(_jawL, _model.Sides[1], closed);
        }

        private static void Place(Transform t, NVector3 position, NQuaternion rotation)
        {
            t.SetPositionAndRotation(new Vector3(position.X, position.Y, position.Z), new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W));
        }

        private static void SetJaw(Transform jaw, CuffSide side, float closed)
        {
            float degrees = side.HingeAngle * (1f - Mathf.Clamp01(closed)) * Mathf.Rad2Deg;
            jaw.localRotation = Quaternion.AngleAxis(degrees, ToUnity(side.HingeAxis));
        }

        private static Vector3 ToUnity(NVector3 v) => new Vector3(v.X, v.Y, v.Z);

        public void Destroy()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            foreach (var o in _owned) if (o != null) UnityEngine.Object.Destroy(o);
            _owned.Clear();
            _tubes.Clear();
        }
    }
}
