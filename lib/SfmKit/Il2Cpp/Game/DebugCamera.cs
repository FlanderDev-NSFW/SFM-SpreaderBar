using System;
using System.Globalization;
using ExposureUnnoticed2.Object3D.Player.Scripts;
using UnityEngine;

namespace SfmKit.Game
{
    /// <summary>
    /// Diagnostics: points the main camera at her from a fixed side, set just before it renders (call from a Camera.onPreCull
    /// hook), with its own light - for screenshots where the game's camera cannot go (it keeps close to her and stops at walls).
    /// A spec is "side distance [target]": side = front, back, left, right, top or below (her frame); distance in metres
    /// (default 1.2); target = "feet" (between her ankles), a name the caller resolves (the `target` argument of
    /// <see cref="Aim"/>), or nothing (her middle, between the hips and the neck). Empty spec = off (the light is removed).
    /// Verified in game through Binders (body views, ankles) and NipplePiercings (nipple close-ups), 2026-10-25.
    /// </summary>
    internal static class DebugCamera
    {
        private static GameObject _light;

        /// <param name="target">Resolves a target name the kit does not know (null = unknown: her middle).</param>
        public static void Aim(Camera cam, string spec, Func<string, Vector3?> target = null)
        {
            if (cam == null || cam != Camera.main) return;
            if (string.IsNullOrEmpty(spec))
            {
                if (_light != null) { UnityEngine.Object.Destroy(_light); _light = null; }
                return;
            }
            var player = PlayerController.Instance;
            var anim = player == null || player.Pca == null ? null : player.Pca.Animator;
            if (anim == null) return;
            Transform root = player.transform;
            string[] p = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            float dist = p.Length > 1 && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float d) ? d : 1.2f;
            Vector3 dir = p[0] switch
            {
                "left" => -root.right, "front" => root.forward, "back" => -root.forward, "top" => root.up, "below" => -root.up,
                _ => root.right,
            };
            string name = p.Length > 2 ? p[2] : "";
            Vector3? at = name == "feet"
                ? (anim.GetBoneTransform(HumanBodyBones.LeftFoot).position + anim.GetBoneTransform(HumanBodyBones.RightFoot).position) / 2f
                : name.Length > 0 && target != null ? target(name) : null;
            Vector3 aim = at ?? (anim.GetBoneTransform(HumanBodyBones.Hips).position + anim.GetBoneTransform(HumanBodyBones.Neck).position) / 2f;
            bool vertical = p[0] == "top" || p[0] == "below";
            Vector3 pos = aim + dir * dist + (vertical || at != null ? Vector3.zero : root.up * 0.1f);
            cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(aim - pos, vertical ? root.forward : root.up));
            if (_light == null)
            {
                _light = new GameObject("SfmKit.DebugLight");
                var light = _light.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
            }
            // From over the camera's shoulder, a little from above.
            _light.transform.rotation = Quaternion.LookRotation(aim - pos - root.up * 0.5f * dist, root.up);
        }
    }
}
