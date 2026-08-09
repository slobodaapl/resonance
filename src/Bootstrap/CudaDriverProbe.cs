using System.Runtime.InteropServices;

namespace Resonance.Bootstrap;

internal static class CudaDriverProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string moduleName);

    internal static bool IsAvailable()
    {
        var isWineRuntime = IsWineRuntime();
        var bridgeFileExists = File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));
        if (isWineRuntime) return IsAvailable(false, true, bridgeFileExists);

        var driverExportAvailable = false;
        if (NativeLibrary.TryLoad("nvcuda.dll", out var library))
        {
            try { driverExportAvailable = NativeLibrary.TryGetExport(library, "cuInit", out _); }
            finally { NativeLibrary.Free(library); }
        }

        return IsAvailable(driverExportAvailable, false, bridgeFileExists);
    }

    internal static bool IsAvailable(bool driverExportAvailable, bool isWineRuntime, bool bridgeFileExists) =>
        driverExportAvailable || (isWineRuntime && bridgeFileExists);

    private static bool IsWineRuntime()
    {
        var library = GetModuleHandleW("ntdll.dll");
        return library != 0 && NativeLibrary.TryGetExport(library, "wine_get_version", out _);
    }
}
