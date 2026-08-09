using System.Text;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VGAudio.Containers.Hca;
using VGAudio.Formats.Pcm16;

namespace Resonance.Game;

internal static class ScdAudioDecoder
{
    private const int VorbisFormat = 0x06;
    private const int AdpcmFormat = 0x0c;
    private const int HcaFormat = 0x1a;

    internal static float[] Extract(byte[] scd, uint soundNumber, CancellationToken token)
    {
        using var stream = new MemoryStream(scd, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        if (scd.Length < 0x50 || Encoding.ASCII.GetString(scd, 0, 8) != "SEDBSSCF")
            throw new InvalidDataException("Invalid SCD header");
        stream.Position = 0x30;
        var soundCount = ReadCount(reader);
        var trackCount = ReadCount(reader);
        var audioCount = ReadCount(reader);
        reader.ReadInt16();
        stream.Position += 24;
        ReadOffsets(reader, soundCount);
        ReadOffsets(reader, trackCount);
        var audioOffsets = ReadOffsets(reader, audioCount);
        if (soundNumber >= audioOffsets.Length || audioOffsets[soundNumber] <= 0)
            throw new InvalidDataException($"SCD audio entry {soundNumber} is unavailable");

        stream.Position = audioOffsets[soundNumber];
        var dataLength = ReadNonNegative(reader, "audio data length");
        var channels = ReadPositive(reader, "channel count", 16);
        var sampleRate = ReadPositive(reader, "sample rate", 384000);
        var format = reader.ReadInt32();
        stream.Position += 8;
        var subInfoSize = ReadNonNegative(reader, "sub-info size");
        var flags = reader.ReadInt32();
        var markerSize = 0;
        if ((flags & 1) != 0)
        {
            var markerStart = stream.Position;
            EnsureRemaining(stream, 8);
            stream.Position += 4;
            markerSize = ReadNonNegative(reader, "marker size");
            if (markerSize < 20) throw new InvalidDataException("Invalid SCD marker size");
            stream.Position = checked(markerStart + markerSize);
        }
        if (markerSize > subInfoSize) throw new InvalidDataException("SCD marker exceeds sub-info");
        token.ThrowIfCancellationRequested();

        return format switch
        {
            VorbisFormat => DecodeVorbis(reader, dataLength, token),
            AdpcmFormat => DecodeAdpcm(reader, dataLength, subInfoSize - markerSize, token),
            HcaFormat => DecodeHca(reader, dataLength, token),
            _ => throw new NotSupportedException($"SCD audio format 0x{format:x} is not supported"),
        };
    }

    private static float[] DecodeVorbis(BinaryReader reader, int dataLength, CancellationToken token)
    {
        EnsureRemaining(reader.BaseStream, 32);
        var encodeMode = reader.ReadInt16();
        var encodeByte = reader.ReadInt16();
        reader.ReadInt32();
        reader.ReadInt32();
        reader.ReadSingle();
        var seekTableSize = ReadNonNegative(reader, "Vorbis seek table size");
        var headerSize = ReadNonNegative(reader, "Vorbis header size");
        reader.ReadInt32();
        reader.ReadInt32();
        if (seekTableSize % 4 != 0) throw new InvalidDataException("Invalid Vorbis seek table size");
        EnsureRemaining(reader.BaseStream, checked((long)seekTableSize + headerSize + dataLength));
        reader.BaseStream.Position += seekTableSize;
        var header = reader.ReadBytes(headerSize);
        if (encodeMode == 0x2002 && encodeByte != 0)
            for (var index = 0; index < header.Length; index++) header[index] ^= (byte)encodeByte;
        var body = reader.ReadBytes(dataLength);
        var ogg = new byte[checked(header.Length + body.Length)];
        Buffer.BlockCopy(header, 0, ogg, 0, header.Length);
        Buffer.BlockCopy(body, 0, ogg, header.Length, body.Length);
        if (encodeMode == 0x2003) DecodeVorbisTable(ogg, body.Length);
        else if (encodeMode is not 0 and not 0x2002)
            throw new InvalidDataException($"Unsupported SCD Vorbis encoding 0x{encodeMode:x}");
        using var wave = new VorbisWaveReader(new MemoryStream(ogg, false));
        return DecodeWave(wave, token);
    }

    private static float[] DecodeAdpcm(BinaryReader reader, int dataLength, int waveHeaderSize, CancellationToken token)
    {
        if (waveHeaderSize <= 0) throw new InvalidDataException("Missing ADPCM format header");
        EnsureRemaining(reader.BaseStream, checked((long)waveHeaderSize + dataLength));
        var header = reader.ReadBytes(waveHeaderSize);
        using var headerStream = new MemoryStream(header, false);
        using var format = new BinaryReader(headerStream);
        if (format.ReadUInt16() != 2) throw new InvalidDataException("SCD ADPCM is not Microsoft ADPCM");
        var channels = format.ReadUInt16();
        var sampleRate = format.ReadInt32();
        format.ReadInt32();
        var blockAlign = format.ReadUInt16();
        if (format.ReadUInt16() != 4) throw new InvalidDataException("Invalid ADPCM bit depth");
        var extraSize = format.ReadUInt16();
        var samplesPerBlock = format.ReadUInt16();
        var coefficientCount = format.ReadUInt16();
        if (channels is < 1 or > 2 || sampleRate <= 0 || blockAlign < channels * 7
            || extraSize < 4 + coefficientCount * 4 || coefficientCount == 0)
            throw new InvalidDataException("Invalid Microsoft ADPCM format header");
        var coefficients = new (short First, short Second)[coefficientCount];
        for (var index = 0; index < coefficientCount; index++)
            coefficients[index] = (format.ReadInt16(), format.ReadInt16());
        var data = reader.ReadBytes(dataLength);
        var decoded = DecodeMicrosoftAdpcm(data, channels, blockAlign, samplesPerBlock, coefficients, token);
        return Resample(decoded, sampleRate, 24000);
    }

    private static float[] DecodeMicrosoftAdpcm(byte[] data, int channels, int blockAlign, int samplesPerBlock,
        (short First, short Second)[] coefficients, CancellationToken token)
    {
        int[] adaptation = [230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230];
        var output = new List<float>((data.Length / blockAlign + 1) * samplesPerBlock);
        for (var blockOffset = 0; blockOffset + channels * 7 <= data.Length; blockOffset += blockAlign)
        {
            token.ThrowIfCancellationRequested();
            var blockLength = Math.Min(blockAlign, data.Length - blockOffset);
            using var stream = new MemoryStream(data, blockOffset, blockLength, false);
            using var block = new BinaryReader(stream);
            var predictors = new byte[channels];
            var deltas = new int[channels];
            var sample1 = new int[channels];
            var sample2 = new int[channels];
            for (var channel = 0; channel < channels; channel++)
            {
                predictors[channel] = block.ReadByte();
                if (predictors[channel] >= coefficients.Length) throw new InvalidDataException("Invalid ADPCM predictor");
            }
            for (var channel = 0; channel < channels; channel++) deltas[channel] = Math.Max(16, (int)block.ReadInt16());
            for (var channel = 0; channel < channels; channel++) sample1[channel] = block.ReadInt16();
            for (var channel = 0; channel < channels; channel++) sample2[channel] = block.ReadInt16();
            AppendFrame(output, sample2, channels);
            AppendFrame(output, sample1, channels);
            var frames = 2;
            while (stream.Position < stream.Length && frames < samplesPerBlock)
            {
                var packed = block.ReadByte();
                if (channels == 1)
                {
                    DecodeNibble((packed >> 4) & 0xf, 0);
                    frames++;
                    if (frames < samplesPerBlock)
                    {
                        DecodeNibble(packed & 0xf, 0);
                        frames++;
                    }
                }
                else
                {
                    DecodeNibble((packed >> 4) & 0xf, 0);
                    DecodeNibble(packed & 0xf, 1);
                    frames++;
                }
            }

            void DecodeNibble(int nibble, int channel)
            {
                var coefficient = coefficients[predictors[channel]];
                var signed = nibble >= 8 ? nibble - 16 : nibble;
                var prediction = (sample1[channel] * coefficient.First + sample2[channel] * coefficient.Second) / 256;
                var sample = Math.Clamp(prediction + signed * deltas[channel], short.MinValue, short.MaxValue);
                sample2[channel] = sample1[channel];
                sample1[channel] = sample;
                deltas[channel] = Math.Max(16, adaptation[nibble] * deltas[channel] / 256);
                if (channels == 1) output.Add(sample / 32768f);
                else if (channel == 1) output.Add((sample1[0] + sample1[1]) / 65536f);
            }
        }
        return output.ToArray();
    }

    private static void AppendFrame(List<float> output, int[] samples, int channels)
    {
        var sum = 0;
        for (var channel = 0; channel < channels; channel++) sum += samples[channel];
        output.Add(sum / (32768f * channels));
    }

    private static float[] DecodeHca(BinaryReader reader, int dataLength, CancellationToken token)
    {
        EnsureRemaining(reader.BaseStream, 24);
        reader.BaseStream.Position += 2;
        var headerSize = reader.ReadInt16();
        var blockSize = reader.ReadInt16();
        reader.BaseStream.Position += 7;
        var plainText = reader.ReadByte() != 0;
        reader.BaseStream.Position += 10;
        if (headerSize <= 0 || blockSize <= 0) throw new InvalidDataException("Invalid HCA sub-info");
        EnsureRemaining(reader.BaseStream, checked((long)headerSize + dataLength));
        var header = reader.ReadBytes(headerSize);
        var body = reader.ReadBytes(dataLength);
        if (!plainText) DecodeHcaTable(body, blockSize, dataLength, headerSize);
        var hca = new byte[checked(header.Length + body.Length)];
        Buffer.BlockCopy(header, 0, hca, 0, header.Length);
        Buffer.BlockCopy(body, 0, hca, header.Length, body.Length);
        token.ThrowIfCancellationRequested();
        var pcm = new HcaReader().Read(hca).GetFormat<Pcm16Format>();
        if (pcm.Channels.Length == 0) throw new InvalidDataException("HCA contains no channels");
        var sampleCount = pcm.Channels.Min(channel => channel.Length);
        var mono = new float[sampleCount];
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var sum = 0f;
            for (var channel = 0; channel < pcm.Channels.Length; channel++) sum += pcm.Channels[channel][sample] / 32768f;
            mono[sample] = sum / pcm.Channels.Length;
        }
        return Resample(mono, pcm.SampleRate, 24000);
    }

    private static float[] DecodeWave(WaveStream wave, CancellationToken token)
    {
        var provider = wave.ToSampleProvider();
        var interleaved = new List<float>();
        var buffer = new float[8192];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            interleaved.AddRange(buffer.AsSpan(0, read));
        }
        var channels = wave.WaveFormat.Channels;
        var mono = new float[interleaved.Count / channels];
        for (var frame = 0; frame < mono.Length; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++) sum += interleaved[frame * channels + channel];
            mono[frame] = sum / channels;
        }
        return Resample(mono, wave.WaveFormat.SampleRate, 24000);
    }

    private static float[] Resample(float[] source, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate) return source;
        var provider = new ArraySampleProvider(source, sourceRate);
        var resampler = new WdlResamplingSampleProvider(provider, targetRate);
        var result = new List<float>((int)Math.Ceiling(source.Length * (double)targetRate / sourceRate));
        var buffer = new float[4096];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0) result.AddRange(buffer.AsSpan(0, read));
        return result.ToArray();
    }

    private static int[] ReadOffsets(BinaryReader reader, int count)
    {
        EnsureRemaining(reader.BaseStream, checked((long)count * 4));
        var values = new int[count];
        for (var index = 0; index < count; index++) values[index] = reader.ReadInt32();
        reader.BaseStream.Position = (reader.BaseStream.Position + 15) & ~15L;
        return values;
    }

    private static int ReadCount(BinaryReader reader) => ReadPositive(reader, "entry count", short.MaxValue, true);
    private static int ReadNonNegative(BinaryReader reader, string name)
    {
        var value = reader.ReadInt32();
        return value >= 0 ? value : throw new InvalidDataException($"Invalid {name}");
    }

    private static int ReadPositive(BinaryReader reader, string name, int maximum, bool allowZero = false)
    {
        var value = name == "entry count" ? reader.ReadInt16() : reader.ReadInt32();
        if (value < (allowZero ? 0 : 1) || value > maximum) throw new InvalidDataException($"Invalid {name}");
        return value;
    }

    private static void EnsureRemaining(Stream stream, long count)
    {
        if (count < 0 || stream.Position < 0 || stream.Position + count > stream.Length)
            throw new EndOfStreamException("Truncated SCD data");
    }

    private sealed class ArraySampleProvider(float[] samples, int sampleRate) : ISampleProvider
    {
        private int position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(count, samples.Length - position);
            samples.AsSpan(position, available).CopyTo(buffer.AsSpan(offset, available));
            position += available;
            return available;
        }
    }

    private static void DecodeVorbisTable(byte[] data, int dataLength)
    {
        var byte1 = dataLength & 0x7f;
        var byte2 = byte1 & 0x3f;
        for (var index = 0; index < data.Length; index++)
            data[index] = (byte)(XorTable[(byte2 + index) & 0xff] ^ data[index] ^ byte1);
    }

    private static void DecodeHcaTable(byte[] data, int blockSize, int dataLength, int seed)
    {
        var length6 = dataLength & 0x3f;
        var length7 = dataLength & 0x7f;
        var position = 0;
        var blockStart = 0;
        while (position + blockSize <= data.Length)
        {
            for (var index = 0; index < blockSize; index++)
                data[position + index] ^= (byte)(XorTable[(seed + index + blockStart + length6) & 0xff] ^ length7);
            position += blockSize;
            blockStart = (blockStart + blockSize) & 0xff;
        }
    }

    // FFXIV SCD Vorbis obfuscation table; adapted from VFXEditor (MIT).
    private static ReadOnlySpan<byte> XorTable =>
    [
        0x3a,0x32,0x32,0x32,0x03,0x7e,0x12,0xf7,0xb2,0xe2,0xa2,0x67,0x32,0x32,0x22,0x32,
        0x32,0x52,0x16,0x1b,0x3c,0xa1,0x54,0x7b,0x1b,0x97,0xa6,0x93,0x1a,0x4b,0xaa,0xa6,
        0x7a,0x7b,0x1b,0x97,0xa6,0xf7,0x02,0xbb,0xaa,0xa6,0xbb,0xf7,0x2a,0x51,0xbe,0x03,
        0xf4,0x2a,0x51,0xbe,0x03,0xf4,0x2a,0x51,0xbe,0x12,0x06,0x56,0x27,0x32,0x32,0x36,
        0x32,0xb2,0x1a,0x3b,0xbc,0x91,0xd4,0x7b,0x58,0xfc,0x0b,0x55,0x2a,0x15,0xbc,0x40,
        0x92,0x0b,0x5b,0x7c,0x0a,0x95,0x12,0x35,0xb8,0x63,0xd2,0x0b,0x3b,0xf0,0xc7,0x14,
        0x51,0x5c,0x94,0x86,0x94,0x59,0x5c,0xfc,0x1b,0x17,0x3a,0x3f,0x6b,0x37,0x32,0x32,
        0x30,0x32,0x72,0x7a,0x13,0xb7,0x26,0x60,0x7a,0x13,0xb7,0x26,0x50,0xba,0x13,0xb4,
        0x2a,0x50,0xba,0x13,0xb5,0x2e,0x40,0xfa,0x13,0x95,0xae,0x40,0x38,0x18,0x9a,0x92,
        0xb0,0x38,0x00,0xfa,0x12,0xb1,0x7e,0x00,0xdb,0x96,0xa1,0x7c,0x08,0xdb,0x9a,0x91,
        0xbc,0x08,0xd8,0x1a,0x86,0xe2,0x70,0x39,0x1f,0x86,0xe0,0x78,0x7e,0x03,0xe7,0x64,
        0x51,0x9c,0x8f,0x34,0x6f,0x4e,0x41,0xfc,0x0b,0xd5,0xae,0x41,0xfc,0x0b,0xd5,0xae,
        0x41,0xfc,0x3b,0x70,0x71,0x64,0x33,0x32,0x12,0x32,0x32,0x36,0x70,0x34,0x2b,0x56,
        0x22,0x70,0x3a,0x13,0xb7,0x26,0x60,0xba,0x1b,0x94,0xaa,0x40,0x38,0x00,0xfa,0xb2,
        0xe2,0xa2,0x67,0x32,0x32,0x12,0x32,0xb2,0x32,0x32,0x32,0x32,0x75,0xa3,0x26,0x7b,
        0x83,0x26,0xf9,0x83,0x2e,0xff,0xe3,0x16,0x7d,0xc0,0x1e,0x63,0x21,0x07,0xe3,0x01
    ];
}
