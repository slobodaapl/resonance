using System.Text;
using Resonance.Game;

namespace Resonance.Tests;

public sealed class ScdAudioDecoderTests
{
    [Fact]
    public void DecodesSelectedMsAdpcmEntryToMono24Khz()
    {
        var scd = CreateAdpcmScd();

        var samples = ScdAudioDecoder.Extract(scd, 0, TestContext.Current.CancellationToken);

        Assert.InRange(samples.Length, 1400, 1600);
        Assert.All(samples, value => Assert.InRange(Math.Abs(value), 0f, 0.0001f));
        Assert.Throws<InvalidDataException>(() =>
            ScdAudioDecoder.Extract(scd, 1, TestContext.Current.CancellationToken));
    }

    private static byte[] CreateAdpcmScd()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Encoding.ASCII.GetBytes("SEDBSSCF"));
        writer.Write(0);
        writer.Write((byte)0);
        writer.Write((byte)4);
        writer.Write((short)0x30);
        writer.Write(0);
        writer.Write(new byte[28]);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)1);
        writer.Write((short)0);
        writer.Write(new byte[24]);
        writer.Write(0x60);
        writer.Write(new byte[12]);

        const int blockSize = 256;
        var format = CreateAdpcmFormat(blockSize);
        writer.Write(blockSize);
        writer.Write(1);
        writer.Write(8000);
        writer.Write(0x0c);
        writer.Write(0);
        writer.Write(0);
        writer.Write(format.Length);
        writer.Write(0);
        writer.Write(format);
        writer.Write((byte)0);
        writer.Write((short)16);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write(new byte[blockSize - 7]);
        return stream.ToArray();
    }

    private static byte[] CreateAdpcmFormat(int blockSize)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((short)2);
        writer.Write((short)1);
        writer.Write(8000);
        writer.Write(4096);
        writer.Write((short)blockSize);
        writer.Write((short)4);
        writer.Write((short)32);
        writer.Write((short)500);
        writer.Write((short)7);
        var coefficients = new (short First, short Second)[]
        {
            (256, 0), (512, -256), (0, 0), (192, 64), (240, 0), (460, -208), (392, -232),
        };
        foreach (var coefficient in coefficients)
        {
            writer.Write(coefficient.First);
            writer.Write(coefficient.Second);
        }
        return stream.ToArray();
    }
}
