using System.Runtime.InteropServices;

namespace Resonance.Bootstrap;

internal static class RuntimeEnvironmentIdentity
{
    private static readonly string[] ProtonCudaVariables =
        ["PROTON_ENABLE_NVAPI", "PROTON_NVIDIA_NVCUDA"];

    [DllImport("ntdll.dll", EntryPoint = "wine_get_version", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint WineGetVersion();

    public static string Get()
    {
        var wine = TryGetWineVersion();
        return $"{RuntimeInformation.OSDescription}|{RuntimeInformation.FrameworkDescription}|" +
               $"{RuntimeInformation.OSArchitecture}|{RuntimeInformation.ProcessArchitecture}|wine={wine ?? "none"}";
    }

    internal static bool IsWine() => TryGetWineVersion() is not null;

    internal static string[] MissingProtonCudaVariables() =>
        MissingProtonCudaVariables(IsWine(), Environment.GetEnvironmentVariable);

    internal static string[] MissingProtonCudaVariables(
        bool isWine, Func<string, string?> getEnvironmentVariable)
    {
        if (!isWine) return [];
        return ProtonCudaVariables
            .Where(name => getEnvironmentVariable(name) != "1")
            .ToArray();
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
