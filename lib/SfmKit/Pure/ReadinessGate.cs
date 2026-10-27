using System;

namespace SfmKit.Pure
{
    /// <summary>
    /// Debounces a "the game world is ready" signal. Right after a scene/stage change the same objects can
    /// exist for a few frames in a half-initialised state; acting on them early causes subtle bugs.
    /// The gate reports ready only when the SAME identity has been observed with the condition true for at
    /// least <c>minFrames</c> distinct frames AND <c>minSeconds</c> of game time.
    ///
    /// Identity should be a few native pointers (<c>obj.Pointer.ToInt64()</c>), never managed wrapper
    /// references: Il2CppInterop may hand out different wrapper objects for the same native object.
    ///
    /// Verification: unit-tested (tests/SfmKit.Tests). Generalised from CollarAsset RigReadinessGate, which
    /// uses 3 frames / 0.5 s and is in-game verified there.
    /// </summary>
    internal sealed class ReadinessGate
    {
        private readonly int _minFrames;
        private readonly float _minSeconds;
        private long[] _identity;
        private float _since;
        private int _lastFrame = -1;
        private int _samples;

        public ReadinessGate(int minFrames = 3, float minSeconds = 0.5f)
        {
            if (minFrames < 1) throw new ArgumentOutOfRangeException(nameof(minFrames));
            if (minSeconds < 0f) throw new ArgumentOutOfRangeException(nameof(minSeconds));
            _minFrames = minFrames;
            _minSeconds = minSeconds;
        }

        /// <summary>Forget everything; the next observation starts a fresh stability window.</summary>
        public void Reset()
        {
            _identity = null;
            _samples = 0;
            _lastFrame = -1;
        }

        /// <param name="identity">Native pointers (or other stable ids) that define "the same world".</param>
        /// <param name="condition">Whether the world currently looks usable (player exists, rig at rest, ...).</param>
        /// <param name="now">Game time in seconds (e.g. Time.unscaledTime).</param>
        /// <param name="frame">Frame counter (e.g. Time.frameCount); several calls in one frame count once.</param>
        public bool Observe(long[] identity, bool condition, float now, int frame)
        {
            if (!float.IsFinite(now)) throw new InvalidOperationException("Invalid readiness clock.");
            if (!condition || identity == null || identity.Length == 0)
            {
                Reset();
                return false;
            }

            bool same = _identity != null && _identity.Length == identity.Length;
            if (same)
            {
                for (int i = 0; i < identity.Length; i++)
                {
                    if (_identity[i] != identity[i]) { same = false; break; }
                }
            }

            if (!same)
            {
                _identity = (long[])identity.Clone();
                _since = now;
                _samples = 0;
                _lastFrame = -1;
            }

            if (frame != _lastFrame)
            {
                _lastFrame = frame;
                _samples++;
            }

            return _samples >= _minFrames && now - _since >= _minSeconds;
        }
    }
}
