using System.Runtime.InteropServices;

namespace RemoteHeavylifter.Worker.Hosting;

/// <summary>The process environment native libraries read. On Unix, .NET keeps its own copy:
/// <see cref="Environment.SetEnvironmentVariable(string, string?)"/> never reaches getenv() in libva and the like.</summary>
internal static partial class NativeEnvironment
{
    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnv(string name, string value, int overwrite);

    /// <summary>Sets <paramref name="name"/> for native code unless it is already set.</summary>
    public static void SetDefault(string name, string value)
    {
        if (OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable(name) is not null)
            return;
        SetEnv(name, value, overwrite: 0);
        Environment.SetEnvironmentVariable(name, value);
    }
}
