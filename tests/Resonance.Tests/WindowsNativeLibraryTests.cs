using Resonance.Tts;

namespace Resonance.Tests;

public sealed class WindowsNativeLibraryTests
{
    private static readonly string SelectedPack = Path.Combine(Path.GetTempPath(), "runtime-pack-current");
    private static readonly string PreviousPack = Path.Combine(Path.GetTempPath(), "runtime-pack-previous");

    [Fact]
    public void ManagedReloadAcceptsNativeModulesFromTheSelectedPack()
    {
        WindowsNativeLibrary.RequireCompatibleRuntime(SelectedPack,
        [
            Path.Combine(SelectedPack, "qwen.dll"),
            Path.Combine(SelectedPack, "GGML.DLL"),
            Path.Combine(SelectedPack, "ggml-base.dll"),
            Path.Combine(PreviousPack, "unrelated.dll"),
        ]);
    }

    [Theory]
    [InlineData("qwen.dll")]
    [InlineData("ggml.dll")]
    [InlineData("ggml-base.dll")]
    public void PackSwitchRequiresRestartWhileAnOldModuleIsStillLoaded(string module)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            WindowsNativeLibrary.RequireCompatibleRuntime(SelectedPack, [Path.Combine(PreviousPack, module)]));
        Assert.Contains(module, error.Message);
        Assert.Contains("relaunch the game", error.Message);
    }

    [Fact]
    public void ReloadRejectsAConflictingModuleEvenWhenTheSelectedCopyIsLoaded()
    {
        Assert.Throws<InvalidOperationException>(() => WindowsNativeLibrary.RequireCompatibleRuntime(SelectedPack,
        [
            Path.Combine(SelectedPack, "ggml-base.dll"),
            Path.Combine(PreviousPack, "ggml-base.dll"),
        ]));
    }
}
