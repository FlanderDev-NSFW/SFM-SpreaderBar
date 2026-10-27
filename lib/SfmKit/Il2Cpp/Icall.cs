using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace SfmKit.Il2Cpp
{
    /// <summary>
    /// Direct access to Unity internal calls (icalls) by registration name, e.g.
    /// "UnityEngine.Cursor::get_visible". Bypasses the generated interop wrappers, which is required when
    /// the wrapper marshals a native type incorrectly (see NativeBool) or when a native detour is needed.
    ///
    /// Verification: the same IL2CPP.il2cpp_resolve_icall + Marshal.GetDelegateForFunctionPointer pattern is
    /// compiled and in-game verified in CollarAsset (CursorLease, NativeFlags, C3MovementHook).
    /// </summary>
    internal static class Icall
    {
        /// <summary>Resolve an icall pointer or throw. Returns a non-zero pointer.</summary>
        public static IntPtr Resolve(string name)
        {
            IntPtr pointer = IL2CPP.il2cpp_resolve_icall(name);
            if (pointer == IntPtr.Zero) throw new MissingMethodException("Unity icall not found: " + name);
            return pointer;
        }

        /// <summary>Resolve an icall and wrap it in a delegate type that matches its native signature.
        /// The delegate type must be marked [UnmanagedFunctionPointer(CallingConvention.Cdecl)].</summary>
        public static T Bind<T>(string name) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(Resolve(name));
    }

    /// <summary>
    /// Reads/writes Unity native bool properties through their icalls using a ONE-BYTE bool.
    /// Unity's native bool is 1 byte; the generated bool getters can read it wrongly (CollarAsset NativeFlags
    /// documents this). Example: NativeBool.Get(renderer, "UnityEngine.Renderer::get_enabled").
    ///
    /// Verification: pattern copied from CollarAsset (in-game verified there); this wrapper itself is
    /// compile-verified only until a mod using it has been accepted in game.
    /// </summary>
    internal static class NativeBool
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte Getter(IntPtr instance);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Setter(IntPtr instance, byte value);

        private static readonly Dictionary<string, Getter> Getters = new Dictionary<string, Getter>();
        private static readonly Dictionary<string, Setter> Setters = new Dictionary<string, Setter>();

        public static bool Get(IntPtr instance, string icallName)
        {
            if (instance == IntPtr.Zero) throw new ArgumentException("Null native instance.", nameof(instance));
            Getter call;
            lock (Getters)
            {
                if (!Getters.TryGetValue(icallName, out call))
                {
                    call = Icall.Bind<Getter>(icallName);
                    Getters[icallName] = call;
                }
            }
            return call(instance) != 0;
        }

        public static bool Get(Il2CppObjectBase instance, string icallName) => Get(instance.Pointer, icallName);

        public static void Set(IntPtr instance, string icallName, bool value)
        {
            if (instance == IntPtr.Zero) throw new ArgumentException("Null native instance.", nameof(instance));
            Setter call;
            lock (Setters)
            {
                if (!Setters.TryGetValue(icallName, out call))
                {
                    call = Icall.Bind<Setter>(icallName);
                    Setters[icallName] = call;
                }
            }
            call(instance, value ? (byte)1 : (byte)0);
        }

        public static void Set(Il2CppObjectBase instance, string icallName, bool value) => Set(instance.Pointer, icallName, value);
    }
}
