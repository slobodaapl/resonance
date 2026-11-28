using System.Runtime.InteropServices;
using Resonance.Tts;

namespace Resonance.Bootstrap;

internal static class CudaDriverProbe
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Initialize(uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetDeviceCount(out int count);

    internal static (bool BridgeAvailable, bool DeviceAvailable) Probe()
    {
        nint library = 0;
        var bridgeAvailable = false;
        try
        {
            library = WindowsNativeLibrary.LoadCudaDriver();
            bridgeAvailable = NativeLibrary.TryGetExport(library, "cuInit", out var initialize);
            if (!bridgeAvailable || !NativeLibrary.TryGetExport(library, "cuDeviceGetCount", out var getDeviceCount))
                return (bridgeAvailable, false);
            return (true, Marshal.GetDelegateForFunctionPointer<Initialize>(initialize)(0) == 0
                && Marshal.GetDelegateForFunctionPointer<GetDeviceCount>(getDeviceCount)(out var count) == 0
                && count > 0);
        }
        catch { return (bridgeAvailable, false); }
        finally
        {
            if (library != 0) NativeLibrary.Free(library);
        }
    }
}
