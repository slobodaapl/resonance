using System.Runtime.InteropServices;

namespace Resonance.Bootstrap;

internal static class CudaDriverProbe
{
    internal static bool IsAvailable()
    {
        if (!NativeLibrary.TryLoad("nvcuda.dll", out var library)) return false;
        try { return NativeLibrary.TryGetExport(library, "cuInit", out _); }
        finally { NativeLibrary.Free(library); }
    }
}
