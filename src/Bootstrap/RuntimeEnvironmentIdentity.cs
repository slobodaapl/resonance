using System.Runtime.InteropServices;

namespace Resonance.Bootstrap;

internal static class RuntimeEnvironmentIdentity
{
    [DllImport("ntdll.dll", EntryPoint = "wine_get_version", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint WineGetVersion();

    public static string Get()
    {
        var wine = TryGetWineVersion();
        return $"{RuntimeInformation.OSDescription}|{RuntimeInformation.FrameworkDescription}|" +
               $"{RuntimeInformation.OSArchitecture}|{RuntimeInformation.ProcessArchitecture}|wine={wine ?? "none"}";
    }

    private static string? TryGetWineVersion()
    {
        try
        {
            var pointer = WineGetVersion();
            return pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer);
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        catch (BadImageFormatException) { return null; }
    }
}
