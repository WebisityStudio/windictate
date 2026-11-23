using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace WinDictate.Core;

/// <summary>
/// Checks, before anything is loaded, that this PC can run the speech engine:
/// the processor must support AVX, AVX2 and FMA (whisper.cpp's CPU build
/// would otherwise crash with an illegal instruction rather than fail cleanly),
/// and the Microsoft Visual C++ 2015-2022 x64 runtime must be installed (the
/// speech engine's native libraries load it). Each problem gets a plain message.
/// </summary>
public static class SystemCheck
{
    /// <summary>The Visual C++ runtime libraries the speech engine's native DLLs import.</summary>
    public static readonly IReadOnlyList<string> RequiredRuntimeLibraries = new[]
    {
        "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "vcomp140.dll"
    };

    /// <summary>True when the processor has the instructions the speech engine is built for.</summary>
    public static bool CpuSupported() => Avx.IsSupported && Avx2.IsSupported && Fma.IsSupported;

    /// <summary>The required runtime libraries Windows cannot find. Windows only.</summary>
    public static IReadOnlyList<string> MissingRuntimeLibraries()
    {
        var missing = new List<string>();
        foreach (string library in RequiredRuntimeLibraries)
        {
            if (NativeLibrary.TryLoad(library, out IntPtr handle)) NativeLibrary.Free(handle);
            else missing.Add(library);
        }
        return missing;
    }

    /// <summary>
    /// The message to show, or null when this PC is fine. Pure, so it is tested on any OS.
    /// </summary>
    public static string? Problem(bool cpuSupported, IReadOnlyCollection<string> missingLibraries)
    {
        if (!cpuSupported)
            return "This PC's processor is not supported. WinDictate's speech engine needs a processor " +
                   "with AVX2 and FMA instructions (most Intel and AMD processors made since 2015 have them).";
        if (missingLibraries.Count > 0)
            return "The Microsoft Visual C++ 2015-2022 Redistributable (x64) is not installed on this PC, " +
                   "so the speech engine cannot start. Ask IT to install it, then start WinDictate again. " +
                   $"Missing: {string.Join(", ", missingLibraries)}.";
        return null;
    }
}
