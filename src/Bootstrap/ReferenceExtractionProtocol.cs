using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Resonance.Bootstrap;

public sealed record ReferenceExtractionRequest(
    int SchemaVersion,
    int Abi,
    string RuntimeDirectory,
    string TalkerPath,
    string CodecPath,
    string BackendName,
    string InputPcmPath,
    string OutputPath,
    string Transcript,
    string TrustedRuntimeRoot,
    string TrustedModelRoot,
    string TrustedReferenceRoot,
    string RequestNonce,
    string TrustedHelperRoot);

public sealed record ReferenceExtractionOwnership(
    int ProcessId,
    long ProcessStartUtcTicks,
    string RequestNonce);

public sealed record ReferenceExtractionResponse(
    int SchemaVersion,
    int Abi,
    int SpeakerDimension,
    int ReferenceLength,
    int Codebooks,
    string Transcript,
    float[] SpeakerEmbedding,
    int[] RvqCodes);

public static class ReferenceExtractionProtocol
{
    public const int SchemaVersion = 1;
    public const int AbiVersion = 6;
    public const int SampleRate = 24_000;
    public const int MaximumSeconds = 12;
    public const int MaximumSamples = SampleRate * MaximumSeconds;
    public const int MaximumTranscriptCharacters = 20_000;
    public const int MaximumRequestBytes = 128 * 1024;
    public const int MaximumResponseBytes = 16 * 1024 * 1024;

    public static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        WriteIndented = false,
    };

    public static void ValidateRequest(
        ReferenceExtractionRequest request, bool requireInput, bool validateHelperIdentity = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != SchemaVersion || request.Abi != AbiVersion)
            throw new InvalidDataException("Reference extraction request schema or ABI mismatch");
        var runtimeRoot = ValidateDirectory(request.TrustedRuntimeRoot, "trusted runtime root");
        var modelRoot = ValidateDirectory(request.TrustedModelRoot, "trusted model root");
        var referenceRoot = ValidateDirectory(request.TrustedReferenceRoot, "trusted reference root");
        if (validateHelperIdentity && !String.IsNullOrWhiteSpace(request.TrustedHelperRoot))
        {
            var helperRoot = ValidateDirectory(request.TrustedHelperRoot, "trusted helper root");
            var processPath = Environment.ProcessPath
                ?? throw new InvalidDataException("reference extraction helper identity is unavailable");
            var entryPath = Assembly.GetEntryAssembly()?.Location;
            var identityPath = !String.IsNullOrWhiteSpace(entryPath) && File.Exists(entryPath)
                ? entryPath : processPath;
            var helperPath = ValidateFilePath(identityPath, "helper executable");
            RequireWithin(helperPath, helperRoot, "helper executable");
        }
        var runtimeDirectory = ValidateDirectory(request.RuntimeDirectory, "runtime directory");
        var talkerPath = ValidateFilePath(request.TalkerPath, "talker path");
        var codecPath = ValidateFilePath(request.CodecPath, "codec path");
        if (String.IsNullOrWhiteSpace(request.BackendName) || request.BackendName.Length > 256)
            throw new InvalidDataException("Reference extraction backend name is invalid");
        var inputPcmPath = ValidateFilePath(request.InputPcmPath, "input PCM path");
        var outputPath = ValidateFilePath(request.OutputPath, "output path", requireExisting: false);
        ValidateTransientPath(request.InputPcmPath + ".part", referenceRoot, "input PCM temporary path");
        ValidateTransientPath(request.OutputPath + ".part", referenceRoot, "output temporary path");
        RequireWithin(runtimeDirectory, runtimeRoot, "runtime directory", allowEqual: true);
        ValidateTrustedRuntimeLibraries(runtimeDirectory);
        RequireWithin(talkerPath, modelRoot, "talker path");
        RequireWithin(codecPath, modelRoot, "codec path");
        RequireWithin(inputPcmPath, referenceRoot, "input PCM path");
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidDataException("Reference extraction output directory is missing");
        RequireWithin(outputDirectory, referenceRoot, "output path", allowEqual: true);
        if (String.Equals(inputPcmPath, outputPath,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Reference extraction input and output paths must differ");
        if (String.IsNullOrWhiteSpace(request.Transcript)
            || request.Transcript.Length > MaximumTranscriptCharacters)
            throw new InvalidDataException("Reference extraction transcript is invalid");
        if (String.IsNullOrWhiteSpace(request.RequestNonce) || request.RequestNonce.Length > 128
            || request.RequestNonce.Any(char.IsControl)
            || !Guid.TryParseExact(request.RequestNonce, "N", out _))
            throw new InvalidDataException("Reference extraction request nonce is invalid");
        if (requireInput)
        {
            var length = new FileInfo(request.InputPcmPath).Length;
            if (length <= 0 || length > MaximumSamples * sizeof(float) || length % sizeof(float) != 0)
                throw new InvalidDataException("Reference extraction PCM size is invalid");
        }
    }

    public static ReferenceExtractionResponse ParseResponse(string json, string transcript)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(transcript);
        var response = JsonSerializer.Deserialize<ReferenceExtractionResponse>(json, JsonOptions())
            ?? throw new InvalidDataException("Reference extraction response is empty");
        if (response.SchemaVersion != SchemaVersion || response.Abi != AbiVersion
            || !String.Equals(response.Transcript, transcript, StringComparison.Ordinal)
            || response.SpeakerEmbedding is null || response.RvqCodes is null
            || response.SpeakerEmbedding.Length != response.SpeakerDimension)
            throw new InvalidDataException("Reference extraction response metadata or samples are invalid");
        BaseHostProtocol.ValidateVoiceReferencePayload(new BaseHostReferencePayload(
            response.SpeakerEmbedding, response.RvqCodes, response.ReferenceLength,
            response.Codebooks, response.Transcript), transcript);
        return response;
    }

    private static string ValidateDirectory(string value, string label)
    {
        if (String.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidDataException($"Reference extraction {label} is not an absolute path");
        RejectTraversal(value, label);
        var path = CanonicalPath(value, label);
        if (!Directory.Exists(path)) throw new FileNotFoundException($"Reference extraction {label} is missing", path);
        RejectReparseComponents(path, label);
        return path;
    }

    private static string ValidateFilePath(string value, string label, bool requireExisting = true)
    {
        if (String.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidDataException($"Reference extraction {label} is not an absolute path");
        RejectTraversal(value, label);
        var path = CanonicalPath(value, label);
        if (requireExisting && !File.Exists(path)) throw new FileNotFoundException($"Reference extraction {label} is missing", path);
        if (!requireExisting && Directory.Exists(path))
            throw new InvalidDataException($"Reference extraction {label} points to a directory");
        RejectReparseComponents(path, label, includeLeaf: true);
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.Exists(parent))
            throw new DirectoryNotFoundException($"Reference extraction {label} parent is missing: {parent}");
        return path;
    }

    public static string ValidateTransientPath(string value, string trustedRoot, string label)
    {
        var root = ValidateDirectory(trustedRoot, $"{label} root");
        if (String.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidDataException($"Reference extraction {label} is not an absolute path");
        RejectTraversal(value, label);
        var path = CanonicalPath(value, label);
        RequireWithin(path, root, label, allowEqual: false);
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.Exists(parent))
            throw new DirectoryNotFoundException($"Reference extraction {label} parent is missing: {parent}");
        RejectReparseComponents(path, label, includeLeaf: true);
        if (Directory.Exists(path))
            throw new InvalidDataException($"Reference extraction {label} points to a directory");
        return path;
    }

    private static string CanonicalPath(string value, string label)
    {
        if (value.Length > 4096)
            throw new InvalidDataException($"Reference extraction {label} path is too long");
        try { return Path.GetFullPath(value); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"Reference extraction {label} path is invalid", error);
        }
    }

    private static void RequireWithin(string path, string root, string label, bool allowEqual = false)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var isEqual = String.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            normalizedRoot, comparison);
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if ((!allowEqual && isEqual) || (!isEqual && !path.StartsWith(prefix, comparison)))
            throw new InvalidDataException($"Reference extraction {label} escapes its trusted root");
    }

    private static void RejectTraversal(string value, string label)
    {
        var segments = value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(segment => String.Equals(segment, "..", StringComparison.Ordinal)))
            throw new InvalidDataException($"Reference extraction {label} contains traversal");
    }

    private static void RejectReparseComponents(string path, string label, bool includeLeaf = true)
    {
        var current = Path.GetPathRoot(path);
        if (String.IsNullOrEmpty(current)) return;
        var relative = path[current.Length..];
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            var isLeaf = index == segments.Length - 1;
            if (isLeaf && !includeLeaf) continue;
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                try
                {
                    if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                        throw new InvalidDataException($"Reference extraction {label} uses a reparse point");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                continue;
            }
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"Reference extraction {label} uses a reparse point");
            }
            catch (FileNotFoundException) when (!isLeaf) { }
        }
    }

    private static void ValidateTrustedRuntimeLibraries(string runtimeDirectory)
    {
        foreach (var path in Directory.EnumerateFiles(runtimeDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var canonical = CanonicalPath(path, "runtime library");
            RejectReparseComponents(canonical, "runtime library");
        }
    }
}

public sealed record BaseHostLaunchRequest(
    int SchemaVersion,
    int Abi,
    string RuntimeDirectory,
    string TalkerPath,
    string CodecPath,
    string BackendName,
    string TrustedRuntimeRoot,
    string TrustedModelRoot,
    string TrustedHostRoot,
    string TrustedReferenceRoot,
    string RequestNonce,
    string TrustedHelperRoot);

public sealed record BaseHostReferencePayload(
    float[] SpeakerEmbedding,
    int[] RvqCodes,
    int RvqLength,
    int Codebooks,
    string Transcript);

public sealed record BaseHostSynthesisPayload(
    string Text,
    string Language,
    BaseHostReferencePayload? Reference,
    string? Instruction,
    long Seed,
    int MaxNewTokens);

public sealed record BaseHostExtractPayload(string InputPcmPath, string Transcript);

public sealed record BaseHostCancelPayload(
    string TargetRequestId,
    BaseHostFrameKind TargetKind = BaseHostFrameKind.Synthesize);

public sealed record BaseHostBenchmarkPayload(IReadOnlyList<string> BackendNames);

public sealed record BaseHostBenchmarkResponse(
    IReadOnlyList<BaseHostBenchmarkResult> Results,
    bool ContextReady,
    string? ActiveBackendId);

public sealed record BaseHostBackendState(bool ContextReady, string? ActiveBackendId);

public sealed record BaseHostReadyPayload(
    string Backend,
    bool ContextReady,
    string? ActiveBackendId);

public sealed record BaseHostAudioPayload(string SamplesBase64, int SampleCount);

public sealed record BaseHostBenchmarkResult(
    string BackendName,
    bool Successful,
    double? InitializationSeconds,
    double? TimeToFirstAudioSeconds,
    double? RealTimeFactor,
    string? Error);

public enum BaseHostFrameKind
{
    Ping = 1,
    Extract = 2,
    Synthesize = 3,
    CancelSynthesis = 4,
    Benchmark = 5,
    SwitchBackend = 6,
    Shutdown = 7,
    CancelOperation = 9,
    Ready = 20,
    Pong = 21,
    Reference = 22,
    Audio = 23,
    Completed = 24,
    BenchmarkResult = 25,
    BusyExtraction = 26,
    Failed = 27,
    ShutdownAck = 28,
}

public sealed record BaseHostFrame(
    int SchemaVersion,
    int Abi,
    BaseHostFrameKind Kind,
    string RequestId,
    string Payload,
    // Commands carry a strictly increasing session sequence. Responses use zero.
    long Sequence = 0);

internal sealed class BaseHostCommandSequence
{
    private long last;

    internal bool TryAccept(long sequence)
    {
        if (sequence <= 0 || sequence == long.MaxValue) return false;
        var previous = Volatile.Read(ref last);
        if (sequence <= previous) return false;
        Volatile.Write(ref last, sequence);
        return true;
    }
}

public static class BaseHostProtocol
{
    public const int SchemaVersion = 1;
    public const int MaximumFrameBytes = 32 * 1024 * 1024;
    public const int MaximumAudioFrameBytes = 256 * 1024;
    public const int MaximumRequestIdLength = 64;
    public const int MaximumSpeakerEmbeddingValues = 4096;
    public const int MaximumCodebooks = 64;
    // JSON code tokens can require up to ten digits plus sign/comma framing;
    // keep the serialized response below the protocol response budget before
    // any host frame allocation.
    public const int MaximumReferenceCodeCount = ReferenceExtractionProtocol.MaximumResponseBytes / 12;

    public static JsonSerializerOptions JsonOptions() => ReferenceExtractionProtocol.JsonOptions();

    public static string SerializePayload<T>(T payload) =>
        JsonSerializer.Serialize(payload, JsonOptions());

    public static T DeserializePayload<T>(string payload) =>
        JsonSerializer.Deserialize<T>(payload, JsonOptions())
        ?? throw new InvalidDataException("Base runtime host payload is empty");

    public static void ValidateVoiceReferencePayload(
        BaseHostReferencePayload payload, string? expectedTranscript = null,
        int? expectedSpeakerEmbeddingLength = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var speakerEmbedding = payload.SpeakerEmbedding
            ?? throw new InvalidDataException("Voice reference speaker embedding must not be null");
        var rvqCodes = payload.RvqCodes
            ?? throw new InvalidDataException("Voice reference codes must not be null");
        var transcript = payload.Transcript
            ?? throw new InvalidDataException("Voice reference transcript must not be null");
        ValidateVoiceReferenceShape(speakerEmbedding.Length,
            payload.RvqLength, payload.Codebooks, rvqCodes.Length,
            expectedSpeakerEmbeddingLength);
        if (String.IsNullOrWhiteSpace(transcript)
            || transcript.Length > ReferenceExtractionProtocol.MaximumTranscriptCharacters
            || (expectedTranscript is not null
                && !String.Equals(transcript, expectedTranscript, StringComparison.Ordinal)))
            throw new InvalidDataException("Voice reference transcript is invalid");
        if (speakerEmbedding.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Voice reference embedding contains non-finite values");
        if (rvqCodes.Any(value => value < 0))
            throw new InvalidDataException("Voice reference code token is invalid");

        long estimatedBytes;
        try
        {
            estimatedBytes = checked((long)speakerEmbedding.Length * sizeof(float)
                + (long)rvqCodes.Length * sizeof(int)
                + Encoding.UTF8.GetByteCount(transcript));
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException("Voice reference payload size overflows", error);
        }
        if (estimatedBytes > ReferenceExtractionProtocol.MaximumResponseBytes)
            throw new InvalidDataException("Voice reference payload is too large");
    }

    public static void ValidateVoiceReferenceShape(
        int speakerEmbeddingLength, int rvqLength, int codebooks,
        int? codeCount = null, int? expectedSpeakerEmbeddingLength = null)
    {
        if (speakerEmbeddingLength <= 0 || speakerEmbeddingLength > MaximumSpeakerEmbeddingValues)
            throw new InvalidDataException("Voice reference speaker embedding shape is invalid");
        if (expectedSpeakerEmbeddingLength is > 0
            && speakerEmbeddingLength != expectedSpeakerEmbeddingLength.Value)
            throw new InvalidDataException("Voice reference speaker embedding dimension is invalid");
        if (rvqLength <= 0 || rvqLength > ReferenceExtractionProtocol.MaximumSamples
            || codebooks <= 0 || codebooks > MaximumCodebooks)
            throw new InvalidDataException("Voice reference code shape is invalid");
        int expectedCodeCount;
        try { expectedCodeCount = checked(rvqLength * codebooks); }
        catch (OverflowException error)
        {
            throw new InvalidDataException("Voice reference code shape overflows", error);
        }
        if (expectedCodeCount <= 0 || expectedCodeCount > MaximumReferenceCodeCount
            || (codeCount is not null && codeCount.Value != expectedCodeCount))
            throw new InvalidDataException("Voice reference code count is invalid");
    }

    public static void ValidateLaunchRequest(BaseHostLaunchRequest request, bool validateHelperIdentity = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != SchemaVersion || request.Abi != ReferenceExtractionProtocol.AbiVersion)
            throw new InvalidDataException("Base runtime host schema or ABI mismatch");
        var runtimeRoot = ValidateDirectory(request.TrustedRuntimeRoot, "trusted runtime root");
        var modelRoot = ValidateDirectory(request.TrustedModelRoot, "trusted model root");
        var hostRoot = ValidateDirectory(request.TrustedHostRoot, "trusted host root");
        var referenceRoot = ValidateDirectory(request.TrustedReferenceRoot, "trusted reference root");
        var helperRoot = ValidateDirectory(request.TrustedHelperRoot, "trusted helper root");
        if (validateHelperIdentity)
        {
            var processPath = Environment.ProcessPath
                ?? throw new InvalidDataException("Base runtime host identity is unavailable");
            var entryPath = Assembly.GetEntryAssembly()?.Location;
            var identityPath = !String.IsNullOrWhiteSpace(entryPath) && File.Exists(entryPath)
                ? entryPath : processPath;
            var helperPath = ValidateFilePath(identityPath, "host executable");
            RequireWithin(helperPath, helperRoot, "host executable");
        }
        var runtimeDirectory = ValidateDirectory(request.RuntimeDirectory, "runtime directory");
        var talkerPath = ValidateFilePath(request.TalkerPath, "talker path");
        var codecPath = ValidateFilePath(request.CodecPath, "codec path");
        if (String.IsNullOrWhiteSpace(request.BackendName) || request.BackendName.Length > 256
            || request.BackendName.Any(char.IsControl))
            throw new InvalidDataException("Base runtime host backend name is invalid");
        RequireWithin(runtimeDirectory, runtimeRoot, "runtime directory", allowEqual: true);
        RequireWithin(talkerPath, modelRoot, "talker path");
        RequireWithin(codecPath, modelRoot, "codec path");
        RequireWithin(hostRoot, referenceRoot, "host root");
        if (String.IsNullOrWhiteSpace(request.RequestNonce) || request.RequestNonce.Length > 128
            || request.RequestNonce.Any(char.IsControl)
            || !Guid.TryParseExact(request.RequestNonce, "N", out _))
            throw new InvalidDataException("Base runtime host nonce is invalid");
        ValidateTrustedRuntimeLibraries(runtimeDirectory);
    }

    public static void ValidateFrame(BaseHostFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.SchemaVersion != SchemaVersion || frame.Abi != ReferenceExtractionProtocol.AbiVersion)
            throw new InvalidDataException("Base runtime host frame schema or ABI mismatch");
        if (!Enum.IsDefined(frame.Kind))
            throw new InvalidDataException("Base runtime host frame kind is invalid");
        if (String.IsNullOrWhiteSpace(frame.RequestId)
            || frame.RequestId.Length > MaximumRequestIdLength
            || frame.RequestId.Any(char.IsControl)
            || !Guid.TryParseExact(frame.RequestId, "N", out _))
            throw new InvalidDataException("Base runtime host request id is invalid");
        if (String.IsNullOrWhiteSpace(frame.Payload) || frame.Payload.Length > MaximumFrameBytes)
            throw new InvalidDataException("Base runtime host frame payload is invalid");
        if (IsCommand(frame.Kind) && frame.Sequence <= 0)
            throw new InvalidDataException("Base runtime host command sequence is invalid");
        if (!IsCommand(frame.Kind) && frame.Sequence != 0)
            throw new InvalidDataException("Base runtime host response sequence is invalid");
    }

    private static bool IsCommand(BaseHostFrameKind kind) => kind is
        BaseHostFrameKind.Ping or BaseHostFrameKind.Extract or BaseHostFrameKind.Synthesize
        or BaseHostFrameKind.CancelSynthesis or BaseHostFrameKind.CancelOperation
        or BaseHostFrameKind.Benchmark
        or BaseHostFrameKind.SwitchBackend or BaseHostFrameKind.Shutdown;

    public static async Task WriteFrameAsync(Stream stream, BaseHostFrame frame, CancellationToken token)
    {
        ValidateFrame(frame);
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions());
        if (payload.Length > MaximumFrameBytes)
            throw new InvalidDataException("Base runtime host frame is too large");
        var length = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(length, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static void WriteFrame(Stream stream, BaseHostFrame frame)
    {
        ValidateFrame(frame);
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions());
        if (payload.Length > MaximumFrameBytes)
            throw new InvalidDataException("Base runtime host frame is too large");
        var length = BitConverter.GetBytes(payload.Length);
        stream.Write(length, 0, length.Length);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    public static async ValueTask<BaseHostFrame?> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[sizeof(int)];
        var read = await ReadAtMostAsync(stream, header, token).ConfigureAwait(false);
        if (read == 0) return null;
        if (read != header.Length) throw new EndOfStreamException("Base runtime host frame header is truncated");
        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > MaximumFrameBytes)
            throw new InvalidDataException("Base runtime host frame length is invalid");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, token).ConfigureAwait(false);
        var frame = JsonSerializer.Deserialize<BaseHostFrame>(payload, JsonOptions())
            ?? throw new InvalidDataException("Base runtime host frame is empty");
        ValidateFrame(frame);
        return frame;
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
        }
        return total;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Base runtime host frame is truncated");
            total += count;
        }
    }

    private static string ValidateDirectory(string value, string label)
    {
        if (String.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidDataException($"Base runtime host {label} is not an absolute path");
        RejectTraversal(value, label);
        var path = Path.GetFullPath(value);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Base runtime host {label} is missing: {path}");
        RejectReparseComponents(path, label);
        return path;
    }

    private static string ValidateFilePath(string value, string label)
    {
        if (String.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidDataException($"Base runtime host {label} is not an absolute path");
        RejectTraversal(value, label);
        var path = Path.GetFullPath(value);
        if (!File.Exists(path)) throw new FileNotFoundException($"Base runtime host {label} is missing", path);
        RejectReparseComponents(path, label);
        return path;
    }

    private static void ValidateTrustedRuntimeLibraries(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly))
            RejectReparseComponents(Path.GetFullPath(path), "runtime library");
    }

    private static void RequireWithin(string path, string root, string label, bool allowEqual = false)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var equal = String.Equals(normalizedPath, normalizedRoot, comparison);
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if ((!allowEqual && equal) || (!equal && !normalizedPath.StartsWith(prefix, comparison)))
            throw new InvalidDataException($"Base runtime host {label} escapes its trusted root");
    }

    private static void RejectReparseComponents(string path, string label)
    {
        var current = Path.GetPathRoot(path);
        if (String.IsNullOrEmpty(current)) return;
        foreach (var segment in path[current.Length..].Split(
                     Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"Base runtime host {label} uses a reparse point");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void RejectTraversal(string value, string label)
    {
        if (value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => String.Equals(segment, "..", StringComparison.Ordinal)))
            throw new InvalidDataException($"Base runtime host {label} contains traversal");
    }
}
