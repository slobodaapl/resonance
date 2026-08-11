using Resonance.Tts;

namespace Resonance.Tests;

public sealed class QwenRuntimeLeaseTests
{
    [Fact]
    public void NonOwningRuntimeRequiresPluginLifetimeLeaseBeforeNativeConstruction()
    {
        Assert.Throws<InvalidOperationException>(() => new QwenCppRuntime(
            "missing-talker", "missing-codec", "cpu", ownsProcessLease: false));
    }
}
