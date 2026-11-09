using System.Runtime.CompilerServices;
using WinDictate.Core;

namespace WinDictate.Tests;

/// <summary>
/// Points the app's data folder (settings, log, models) at a throwaway temp
/// folder before any test runs, so the suite never reads or writes the real
/// user profile on any OS.
/// </summary>
internal static class TestEnvironment
{
    public static string DataDir { get; } =
        Path.Combine(Path.GetTempPath(), "WinDictate.Tests", Environment.ProcessId.ToString());

#pragma warning disable CA2255 // Module initializer in a test assembly is intentional: it must run before any test.
    [ModuleInitializer]
    internal static void Init()
#pragma warning restore CA2255
    {
        AppSettings.DataDirOverride = DataDir;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(DataDir, recursive: true); } catch { }
        };
    }
}
