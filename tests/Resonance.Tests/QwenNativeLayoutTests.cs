using System.Runtime.InteropServices;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class QwenNativeLayoutTests
{
    [Fact]
    public void AbiSixStructsMatchWinX64NativeLayout()
    {
        Assert.Equal(6, QwenNative.AbiVersion);
        Assert.Equal(24, Marshal.SizeOf<QwenNative.AbiInfo>());
        Assert.Equal(48, Marshal.SizeOf<QwenNative.InitParams>());
        Assert.Equal(24, Marshal.SizeOf<QwenNative.Audio>());
        Assert.Equal(32, Marshal.SizeOf<QwenNative.VoiceRef>());
        Assert.Equal(184, Marshal.SizeOf<QwenNative.TtsParams>());

        Assert.Equal(40, Marshal.OffsetOf<QwenNative.InitParams>(nameof(QwenNative.InitParams.BackendName)).ToInt32());
        Assert.Equal(112, Marshal.OffsetOf<QwenNative.TtsParams>(nameof(QwenNative.TtsParams.DumpDirectory)).ToInt32());
        Assert.Equal(152, Marshal.OffsetOf<QwenNative.TtsParams>(nameof(QwenNative.TtsParams.ReferenceSpeakerEmbedding)).ToInt32());
        Assert.Equal(176, Marshal.OffsetOf<QwenNative.TtsParams>(nameof(QwenNative.TtsParams.ReferenceLength)).ToInt32());
    }
}
