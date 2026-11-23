using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Resonance.Tts;

internal static class WindowsNativeLibrary
{
    internal static void RequireCompatibleRuntime(string runtimeDirectory)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var process = Process.GetCurrentProcess();
        RequireCompatibleRuntime(runtimeDirectory, process.Modules.Cast<ProcessModule>().Select(module => module.FileName));
    }

    internal static void RequireCompatibleRuntime(string runtimeDirectory, IEnumerable<string> loadedModules)
    {
        string[] runtimeModules = ["qwen.dll", "ggml.dll", "ggml-base.dll"];
        var fullDirectory = Path.GetFullPath(runtimeDirectory);
        foreach (var loadedModule in loadedModules)
        {
            var name = Path.GetFileName(loadedModule);
            if (runtimeModules.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !string.Equals(loadedModule, Path.Combine(fullDirectory, name), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"{name} from a different native runtime is still loaded in FFXIV. Exit and relaunch the game to switch runtimes.");
        }
    }

    internal static void RequireUnloaded(IEnumerable<string> modules)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var module in modules)
            if (GetModuleHandle(module) != 0)
                throw new InvalidOperationException(
                    $"{Path.GetFileName(module)} is still loaded in FFXIV. Exit and relaunch the game to unload it.");
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
