using System.Runtime.InteropServices;

namespace Resonance.Tts;

internal static class WindowsNativeLibrary
{
    internal static void RequireUnloaded(IEnumerable<string> modules)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var module in modules)
            if (GetModuleHandle(module) != 0)
                throw new InvalidOperationException(
                    $"Restart FFXIV to finish updating Resonance. {Path.GetFileName(module)} is still loaded.");
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandle(string moduleName);

    internal static nint LoadCudaDriver()
    {
        try { return NativeLibrary.Load("nvcuda.dll"); }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException)
        {
            throw new DllNotFoundException("Unable to load the CUDA driver bridge", error);
        }
    }
}
