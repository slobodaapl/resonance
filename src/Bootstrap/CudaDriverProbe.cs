using System.Runtime.InteropServices;
using Resonance.Tts;

namespace Resonance.Bootstrap;

internal static class CudaDriverProbe
{
    internal static bool IsAvailable()
    {
        nint library = 0;
        try
        {
            library = WindowsNativeLibrary.LoadCudaDriver();
            return NativeLibrary.TryGetExport(library, "cuInit", out _);
        }
        catch { return false; }
        finally
        {
            if (library != 0) NativeLibrary.Free(library);
        }
    }

    internal static bool IsAvailable(bool driverExportAvailable) => driverExportAvailable;

}
