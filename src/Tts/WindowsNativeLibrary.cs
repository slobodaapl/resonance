using System.Runtime.InteropServices;

namespace Resonance.Tts;

internal static class WindowsNativeLibrary
{
    internal static nint LoadCudaDriver()
    {
        try { return NativeLibrary.Load("nvcuda.dll"); }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException)
        {
            throw new DllNotFoundException("Unable to load the CUDA driver bridge", error);
        }
    }
}
