using System.Runtime.InteropServices;

namespace Resonance.Bootstrap;

internal static class RuntimeEnvironmentIdentity
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint WineGetVersion();

    public static string Get()
    {
        var wine = TryGetWineVersion();
        return $"{RuntimeInformation.OSDescription}|{RuntimeInformation.FrameworkDescription}|" +
               $"{RuntimeInformation.OSArchitecture}|{RuntimeInformation.ProcessArchitecture}|wine={wine ?? "none"}";
    }

    private static string? TryGetWineVersion()
    {
        if (!NativeLibrary.TryLoad("ntdll.dll", out var module)) return null;
        if (!NativeLibrary.TryGetExport(module, "wine_get_version", out var export)) return null;
        var pointer = Marshal.GetDelegateForFunctionPointer<WineGetVersion>(export)();
        return pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer);
    }
}
