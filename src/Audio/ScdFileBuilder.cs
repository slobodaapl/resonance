using System.Buffers.Binary;
using System.Security.Cryptography;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Resonance.Audio;

public sealed record PreparedGameMixerAudio(
    float[] Samples,
    int SampleRate,
    bool BaseCloneCorrectionApplied)
{
    public double DurationSeconds => Samples.Length / (double)SampleRate;
}

public sealed record ScdFileAsset(
    ReadOnlyMemory<byte> Bytes,
    string ContentHash,
    string VirtualPath,
    int SampleRate,
    int EncodedSampleCount,
    int SoundCount,
    int TrackCount,
    int AudioCount,
    int AudioFormat);

public readonly record struct ScdAudioLayout(
    int SoundCount,
    int TrackCount,
    int AudioCount,
    int AudioOffset,
    int DataLength,
    int Channels,
    int SampleRate,
    int AudioFormat,
    int SubInfoSize);

public static class GameMixerPcmEncoder
{
    public const int SourceSampleRate = 24_000;
    public const int OutputSampleRate = 44_100;
    public const int MaxDurationSeconds = 120;
    internal const double TargetActiveRmsDbfs = -14.0;
    internal const double MaximumNormalizationGainDb = 6.0;
    internal const double PeakCeilingDbfs = -0.5;

    public static PreparedGameMixerAudio PrepareMono44100(
        ReadOnlySpan<float> samples,
        bool applyBaseCloneCorrection,
        float volume = 1f)
    {
        if (!float.IsFinite(volume) || volume < 0f || volume > 2f)
            throw new ArgumentOutOfRangeException(nameof(volume));
        if (samples.Length > SourceSampleRate * MaxDurationSeconds)
            throw new InvalidDataException("GameMixer source audio exceeds the bounded duration limit");

        var source = samples.ToArray();
        for (var index = 0; index < source.Length; index++)
        {
            if (!float.IsFinite(source[index]))
                throw new InvalidDataException("GameMixer source audio contains a non-finite sample");
        }

        ISampleProvider provider = new ArraySampleProvider(source, SourceSampleRate);
        if (applyBaseCloneCorrection)
        {
            provider = new BaseCloneCorrectionSampleProvider(provider);
            source = Drain(provider);
            NormalizeBaseCloneInPlace(source);
            provider = new ArraySampleProvider(source, SourceSampleRate);
        }
        if (volume != 1f)
            provider = new VolumeSampleProvider(provider) { Volume = volume };
        var resampled = new WdlResamplingSampleProvider(provider, OutputSampleRate);
        var result = new List<float>(Math.Min(
            OutputSampleRate * MaxDurationSeconds,
            checked((int)Math.Ceiling(source.Length * (double)OutputSampleRate / SourceSampleRate) + 512)));
        var buffer = new float[4096];
        int read;
        while ((read = resampled.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < read; index++)
                result.Add(Math.Clamp(buffer[index], -1f, 1f));
            if (result.Count > OutputSampleRate * MaxDurationSeconds)
                throw new InvalidDataException("GameMixer resampled audio exceeds the bounded duration limit");
        }

        return new PreparedGameMixerAudio(result.ToArray(), OutputSampleRate, applyBaseCloneCorrection);
    }

    internal static double NormalizeBaseCloneInPlace(Span<float> samples)
    {
        if (samples.IsEmpty) return 0;
        var peak = 0d;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample))
                throw new InvalidDataException("GameMixer source audio contains a non-finite sample");
            peak = Math.Max(peak, Math.Abs(sample));
        }
        if (peak <= 0) return 0;

        const int frameSamples = SourceSampleRate / 50;
        var threshold = Math.Max(DbToLinear(-50), peak * DbToLinear(-35));
        var activeSquareSum = 0d;
        var activeSampleCount = 0;
        for (var offset = 0; offset < samples.Length; offset += frameSamples)
        {
            var frame = samples.Slice(offset, Math.Min(frameSamples, samples.Length - offset));
            var squareSum = 0d;
            foreach (var sample in frame) squareSum += sample * sample;
            if (Math.Sqrt(squareSum / frame.Length) < threshold) continue;
            activeSquareSum += squareSum;
            activeSampleCount += frame.Length;
        }
        if (activeSampleCount == 0 || activeSquareSum <= 0) return 0;

        var activeRmsDb = LinearToDb(Math.Sqrt(activeSquareSum / activeSampleCount));
        var desiredGainDb = Math.Clamp(
            TargetActiveRmsDbfs - activeRmsDb,
            -MaximumNormalizationGainDb,
            MaximumNormalizationGainDb);
        var peakLimitedGainDb = PeakCeilingDbfs - LinearToDb(peak);
        var gainDb = Math.Min(desiredGainDb, peakLimitedGainDb);
        var gain = DbToLinear(gainDb);
        for (var index = 0; index < samples.Length; index++)
            samples[index] = (float)Math.Clamp(samples[index] * gain, -1d, 1d);
        return gainDb;
    }

    private static float[] Drain(ISampleProvider provider)
    {
        var samples = new List<float>();
        var buffer = new float[4096];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        return samples.ToArray();
    }

    private static double DbToLinear(double value) => Math.Pow(10, value / 20);
    private static double LinearToDb(double value) => 20 * Math.Log10(Math.Max(value, 1e-12));

    private sealed class ArraySampleProvider(float[] samples, int sampleRate) : ISampleProvider
    {
        private int position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(count, samples.Length - position);
            if (available <= 0) return 0;
            samples.AsSpan(position, available).CopyTo(buffer.AsSpan(offset, available));
            position += available;
            return available;
        }
    }
}

public static class ScdFileBuilder
{
    public const int MsAdpcmFormat = 0x0c;
    public const int BlockAlign = 256;
    public const int SamplesPerBlock = 2 + (BlockAlign - 7) * 2;
    public const int MaxScdBytes = 64 * 1024 * 1024;

    private static readonly (short First, short Second)[] Coefficients =
    [
        (256, 0), (512, -256), (0, 0), (192, 64),
        (240, 0), (460, -208), (392, -232),
    ];

    public static ScdFileAsset Build(ReadOnlySpan<float> samples, int sampleRate = GameMixerPcmEncoder.OutputSampleRate)
    {
        if (samples.IsEmpty) throw new ArgumentException("SCD audio cannot be empty", nameof(samples));
        if (sampleRate != GameMixerPcmEncoder.OutputSampleRate)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "GameMixer SCDs are fixed at 44.1 kHz");
        if (samples.Length > sampleRate * GameMixerPcmEncoder.MaxDurationSeconds)
            throw new InvalidDataException("SCD audio exceeds the bounded duration limit");

        var blocks = checked((samples.Length + SamplesPerBlock - 1) / SamplesPerBlock);
        var dataLength = checked(blocks * BlockAlign);
        var format = BuildFormat(sampleRate);
        using var stream = new MemoryStream(0x160 + format.Length + dataLength);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        WriteContainer(writer);
        if (writer.BaseStream.Position > 0x140)
            throw new InvalidDataException($"Internal SCD container layout exceeds audio offset: 0x{writer.BaseStream.Position:x}");
        if (writer.BaseStream.Position < 0x140)
            writer.Write(new byte[checked((int)(0x140 - writer.BaseStream.Position))]);
        writer.Write(dataLength);
        writer.Write(1); // mono
        writer.Write(sampleRate);
        writer.Write(MsAdpcmFormat);
        writer.Write(0); // loop start
        writer.Write(0); // loop end
        writer.Write(format.Length);
        writer.Write(0); // no marker/custom EQ
        writer.Write(format);
        WriteAudio(writer, samples, blocks, SamplesPerBlock);
        writer.Flush();
        writer.BaseStream.Position = 0x10;
        writer.Write(checked((int)writer.BaseStream.Length));
        writer.Flush();

        var bytes = stream.ToArray();
        if (bytes.Length > MaxScdBytes)
            throw new InvalidDataException("SCD file exceeds the bounded size limit");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new ScdFileAsset(
            new ReadOnlyMemory<byte>(bytes),
            hash,
            $"sound/resonance/{hash}.scd",
            sampleRate,
            checked(blocks * SamplesPerBlock),
            1,
            1,
            1,
            MsAdpcmFormat);
    }

    public static ScdFileAsset BuildFromNativeTemplate(
        ReadOnlySpan<float> samples,
        ReadOnlySpan<byte> template,
        int sampleRate = GameMixerPcmEncoder.OutputSampleRate)
    {
        ValidateNativeTemplate(template, out var soundOffset, out var trackOffset, out var audioOffset);
        var channels = ReadInt32(template, audioOffset + 4);
        var nativeSampleRate = ReadInt32(template, audioOffset + 8);
        var format = ReadInt32(template, audioOffset + 12);
        var subInfoSize = ReadInt32(template, audioOffset + 24);
        var formatOffset = checked(audioOffset + 32);
        if (channels != 1 || nativeSampleRate is < 8_000 or > 384_000 || format != MsAdpcmFormat
            || subInfoSize < 22 || formatOffset + subInfoSize > template.Length
            || ReadInt16(template, formatOffset) != 2 || ReadInt16(template, formatOffset + 2) != 1
            || ReadInt16(template, formatOffset + 14) != 4)
            throw new InvalidDataException("Native SCD template is not compatible mono Microsoft ADPCM");
        var blockAlign = ReadInt16(template, formatOffset + 12);
        var samplesPerBlock = ReadInt16(template, formatOffset + 18);
        if (blockAlign < 8 || samplesPerBlock < 4
            || blockAlign != 7 + (samplesPerBlock - 2 + 1) / 2)
            throw new InvalidDataException("Native SCD template has unsupported ADPCM block geometry");

        var blocks = checked((samples.Length + samplesPerBlock - 1) / samplesPerBlock);
        var dataLength = checked(blocks * blockAlign);
        var payloadOffset = checked(formatOffset + subInfoSize);
        var unalignedLength = checked(payloadOffset + dataLength);
        var bytes = new byte[checked((unalignedLength + 15) & ~15)];
        template[..payloadOffset].CopyTo(bytes);
        using (var stream = new MemoryStream(bytes, writable: true))
        using (var writer = new BinaryWriter(stream))
        {
            stream.Position = payloadOffset;
            WriteAudio(writer, samples, blocks, samplesPerBlock);
        }
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(audioOffset), dataLength);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x10), bytes.Length);
        var durationMilliseconds = checked((int)Math.Ceiling(samples.Length * 1000d / sampleRate));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(soundOffset + 0x14), durationMilliseconds);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(trackOffset + 0x50), durationMilliseconds);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(new ReadOnlyMemory<byte>(bytes), hash, $"sound/resonance/{hash}.scd",
            nativeSampleRate, checked(blocks * samplesPerBlock), 1, 1, 1, MsAdpcmFormat);
    }

    public static bool IsNativeTemplateCompatible(ReadOnlySpan<byte> template)
    {
        try
        {
            ValidateNativeTemplate(template, out _, out _, out var audioOffset);
            var formatOffset = checked(audioOffset + 32);
            var subInfoSize = ReadInt32(template, audioOffset + 24);
            if (ReadInt32(template, audioOffset + 4) != 1
                || ReadInt32(template, audioOffset + 8) is < 8_000 or > 384_000
                || ReadInt32(template, audioOffset + 12) != MsAdpcmFormat
                || subInfoSize < 22 || formatOffset + subInfoSize > template.Length
                || ReadInt16(template, formatOffset) != 2
                || ReadInt16(template, formatOffset + 2) != 1
                || ReadInt16(template, formatOffset + 14) != 4)
                return false;
            var blockAlign = ReadInt16(template, formatOffset + 12);
            var samplesPerBlock = ReadInt16(template, formatOffset + 18);
            return blockAlign >= 8 && samplesPerBlock >= 4
                && blockAlign == 7 + (samplesPerBlock - 2 + 1) / 2;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void ValidateNativeTemplate(
        ReadOnlySpan<byte> template,
        out int soundOffset,
        out int trackOffset,
        out int audioOffset)
    {
        if (template.Length < 0x90 || !template[..8].SequenceEqual("SEDBSSCF"u8)
            || ReadInt16(template, 0x30) != 1 || ReadInt16(template, 0x32) != 1
            || ReadInt16(template, 0x34) != 1)
            throw new InvalidDataException("Native SCD template must contain one sound, track, and audio entry");
        soundOffset = ReadInt32(template, 0x50);
        trackOffset = ReadInt32(template, 0x60);
        audioOffset = ReadInt32(template, 0x70);
        if (soundOffset < 0x90 || trackOffset <= soundOffset || audioOffset <= trackOffset
            || audioOffset > template.Length - 32 || soundOffset + 0x18 > template.Length
            || trackOffset + 0x54 > template.Length)
            throw new InvalidDataException("Native SCD template offsets are invalid");
    }

    public static bool TryReadLayout(ReadOnlySpan<byte> scd, out ScdAudioLayout layout, out string? error)
    {
        layout = default;
        error = null;
        try
        {
            if (scd.Length > MaxScdBytes)
                throw new InvalidDataException("SCD file exceeds the bounded size limit");
            if (scd.Length < 0x54 || !scd[..8].SequenceEqual("SEDBSSCF"u8))
                throw new InvalidDataException("Invalid SCD header");
            var soundCount = ReadInt16(scd, 0x30);
            var trackCount = ReadInt16(scd, 0x32);
            var audioCount = ReadInt16(scd, 0x34);
            if (soundCount != 1 || trackCount != 1 || audioCount != 1)
                throw new InvalidDataException("SCD must contain exactly one sound, track, and audio entry");
            var cursor = 0x50;
            cursor = ReadOffsets(scd, cursor, soundCount, out var soundOffsets);
            cursor = ReadOffsets(scd, cursor, trackCount, out var trackOffsets);
            cursor = ReadOffsets(scd, cursor, audioCount, out var audioOffsets);
            cursor = ReadOffsets(scd, cursor, soundCount, out var layoutOffsets);
            if (soundOffsets[0] < cursor || trackOffsets[0] < cursor || layoutOffsets[0] < cursor
                || soundOffsets[0] >= audioOffsets[0] || trackOffsets[0] >= audioOffsets[0]
                || layoutOffsets[0] >= audioOffsets[0]
                || soundOffsets[0] == trackOffsets[0] || soundOffsets[0] == layoutOffsets[0]
                || trackOffsets[0] == layoutOffsets[0])
                throw new InvalidDataException("SCD voice routing entries are outside the supported layout");
            var audioOffset = audioOffsets[0];
            if (audioOffset < cursor || (audioOffset & 15) != 0 || audioOffset > scd.Length - 32)
                throw new InvalidDataException("SCD audio offset is outside the file");
            var dataLength = ReadInt32(scd, audioOffset);
            var channels = ReadInt32(scd, audioOffset + 4);
            var sampleRate = ReadInt32(scd, audioOffset + 8);
            var format = ReadInt32(scd, audioOffset + 12);
            var subInfoSize = ReadInt32(scd, audioOffset + 24);
            if (dataLength <= 0 || dataLength > MaxScdBytes || dataLength % BlockAlign != 0 || channels != 1
                || sampleRate != GameMixerPcmEncoder.OutputSampleRate
                || format != MsAdpcmFormat || subInfoSize <= 0)
                throw new InvalidDataException("SCD audio entry is outside the supported bounded format");
            var formatOffset = checked(audioOffset + 32);
            if (subInfoSize < 50 || formatOffset + subInfoSize > scd.Length
                || ReadInt16(scd, formatOffset) != 2
                || ReadInt16(scd, formatOffset + 2) != 1
                || ReadInt32(scd, formatOffset + 4) != sampleRate
                || ReadInt16(scd, formatOffset + 12) != BlockAlign
                || ReadInt16(scd, formatOffset + 14) != 4
                || ReadInt16(scd, formatOffset + 16) != 4 + Coefficients.Length * 4
                || ReadInt16(scd, formatOffset + 18) != SamplesPerBlock
                || ReadInt16(scd, formatOffset + 20) != Coefficients.Length)
                throw new InvalidDataException("SCD Microsoft ADPCM format header is invalid");
            var payload = checked(audioOffset + 32 + subInfoSize + dataLength);
            if (payload > scd.Length) throw new InvalidDataException("SCD audio payload is truncated");
            layout = new ScdAudioLayout(
                soundCount, trackCount, audioCount, audioOffset, dataLength, channels, sampleRate, format, subInfoSize);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException
                                             or EndOfStreamException or OverflowException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static void WriteContainer(BinaryWriter writer)
    {
        writer.Write("SEDBSSCF"u8.ToArray());
        writer.Write(3);
        writer.Write((byte)0);
        writer.Write((byte)4);
        writer.Write((short)0x30);
        writer.Write(0); // file-size placeholder
        writer.Write(new byte[28]);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write((short)1); // one audio entry
        writer.Write((short)0);
        writer.Write(0x60); // track-offset table
        writer.Write(0x70); // audio-offset table
        writer.Write(0x80); // layout-offset table
        writer.Write(0); // routing-offset table
        writer.Write(0); // attribute-offset table
        writer.Write(0); // EOF padding

        writer.Write(0x90); // sound 0
        writer.Write(new byte[12]);
        writer.Write(0xb0); // track 0
        writer.Write(new byte[12]);
        writer.Write(0x140); // audio 0
        writer.Write(new byte[12]);
        writer.Write(0xc0); // layout 0
        writer.Write(new byte[12]);

        writer.Write((byte)1); // one SoundTrackInfo
        writer.Write((byte)3); // Voice bus
        writer.Write((byte)0xff); // priority
        writer.Write((byte)1); // normal sound
        writer.Write((ushort)0); // no custom routing/effects
        writer.Write(1f);
        writer.Write((short)0);
        writer.Write((byte)0);
        writer.Write((sbyte)0);
        writer.Write((short)0); // track 0
        writer.Write((short)0); // audio 0
        writer.Write(new byte[12]);

        writer.Write((ushort)0); // TrackCmd.End
        writer.Write(new byte[14]);

        writer.Write((ushort)0x80); // fixed-size null/non-positional layout
        writer.Write((byte)0); // SoundObjectType.Null
        writer.Write((byte)1); // version
        writer.Write((byte)0x80); // little-endian
        writer.Write((byte)0);
        writer.Write((short)0);
        writer.Write(0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((short)0);
        writer.Write(1f);
        writer.Write(1f);
        writer.Write(1f);
        writer.Write(1f);
        writer.Write(new byte[96]);
    }

    private static byte[] BuildFormat(int sampleRate)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((short)2); // WAVE_FORMAT_ADPCM
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write((int)Math.Ceiling(sampleRate * (double)BlockAlign / SamplesPerBlock));
        writer.Write((short)BlockAlign);
        writer.Write((short)4);
        writer.Write((short)(4 + Coefficients.Length * 4));
        writer.Write((short)SamplesPerBlock);
        writer.Write((short)Coefficients.Length);
        foreach (var coefficient in Coefficients)
        {
            writer.Write(coefficient.First);
            writer.Write(coefficient.Second);
        }
        return stream.ToArray();
    }

    private static void WriteAudio(
        BinaryWriter writer,
        ReadOnlySpan<float> samples,
        int blocks,
        int samplesPerBlock)
    {
        var adaptation = new[] { 230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230 };
        var signedSamples = new short[checked(blocks * samplesPerBlock)];
        for (var index = 0; index < signedSamples.Length; index++)
        {
            var source = index < samples.Length ? samples[index] : samples[^1];
            signedSamples[index] = (short)Math.Clamp(Math.Round(source * short.MaxValue), short.MinValue, short.MaxValue);
        }

        for (var blockIndex = 0; blockIndex < blocks; blockIndex++)
        {
            var start = blockIndex * samplesPerBlock;
            var first = signedSamples[start];
            var second = signedSamples[start + 1];
            const int predictor = 0;
            var delta = 512;
            writer.Write((byte)predictor);
            writer.Write((short)delta);
            writer.Write(second); // ADPCM sample1; emitted after sample2
            writer.Write(first); // ADPCM sample2; emitted first
            var sample1 = second;
            var nibble = 0;
            for (var frame = 2; frame < samplesPerBlock; frame++)
            {
                var target = signedSamples[start + frame];
                var prediction = sample1;
                var quantized = (int)Math.Round((target - prediction) / (double)delta);
                quantized = Math.Clamp(quantized, -8, 7);
                var unsigned = quantized < 0 ? quantized + 16 : quantized;
                if ((frame & 1) == 0) nibble = unsigned << 4;
                else
                {
                    writer.Write((byte)(nibble | unsigned));
                    nibble = 0;
                }
                var sample = Math.Clamp(prediction + quantized * delta, short.MinValue, short.MaxValue);
                sample1 = (short)sample;
                delta = Math.Max(16, adaptation[unsigned] * delta / 256);
            }
            if ((samplesPerBlock & 1) != 0) writer.Write((byte)nibble);
        }
    }

    private static int ReadOffsets(ReadOnlySpan<byte> data, int cursor, int count, out int[] offsets)
    {
        offsets = new int[count];
        var bytes = checked(count * sizeof(int));
        if (cursor < 0 || cursor + bytes > data.Length) throw new EndOfStreamException("Truncated SCD offsets");
        for (var index = 0; index < count; index++)
            offsets[index] = BinaryPrimitives.ReadInt32LittleEndian(data[(cursor + index * 4)..]);
        cursor = (cursor + bytes + 15) & ~15;
        return cursor;
    }

    private static short ReadInt16(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 2 > data.Length) throw new EndOfStreamException("Truncated SCD header");
        return BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length) throw new EndOfStreamException("Truncated SCD entry");
        return BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
    }
}
