using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

return Generator.Run(args);

internal static class Generator
{
    private const int MaximumReferenceFrames = 192;
    private static readonly string[] Languages = ["english", "japanese", "german", "french"];
    private static readonly Dictionary<string, string> ReferenceTexts = new(StringComparer.Ordinal)
    {
        ["english"] = "Beyond the quiet harbor, bright lanterns shimmer while travelers exchange curious stories of distant roads.",
        ["japanese"] = "静かな港の向こうで明るい灯籠が揺れ、旅人たちは遠い道の不思議な物語を語り合う。",
        ["german"] = "Jenseits des stillen Hafens schimmern helle Laternen, während Reisende neugierige Geschichten ferner Wege erzählen.",
        ["french"] = "Au-delà du port tranquille, de vives lanternes scintillent tandis que les voyageurs racontent d'étranges histoires de routes lointaines.",
    };
    private static readonly Dictionary<string, Anchor> AuthenticAnchors = new(StringComparer.Ordinal)
    {
        ["il_mheg_nu_mou"] = new("beqlugg", null),
        ["il_mheg_pixie"] = new("feoul", null),
        ["loporrit"] = new("livingway", null),
        ["fuath"] = new("bossoffuath", null),
        ["hanuhanu"] = new("hanuhanumaleauda", "masculine"),
        ["vanu_vanu"] = new("linuhanu", "masculine"),
        ["ixal"] = new("ixalichieftain", "masculine"),
        ["amaljaa"] = new("amaljaawarrior", "masculine"),
        ["sahagin"] = new("sahaginpriest", "masculine"),
        ["ondo"] = new("readerofondo", "masculine"),
        ["kojin"] = new("kojinsoldier", "masculine"),
        ["dragon"] = new("tireddragon", "masculine"),
        ["pelupelu"] = new("pelupelumaleauda", "masculine"),
    };

    internal static int Run(string[] args)
    {
        var options = Options.Parse(args);
        var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(options.Catalog), JsonOptions())
                      ?? throw new InvalidDataException("Catalog is empty");
        var qualities = LoadQualities(options);
        using var database = OpenDatabase(options.Database);
        UpdatePackMetadata(database, catalog.Version, options.PackVersion);
        RehashExistingFallbacks(database, catalog.Version);
        using var design = NativeRuntime.Load(options.VoiceDesignModel, qualities[0].Codec, options.Backend,
            options.RuntimeDirectory, voiceDesign: true);
        using var q4 = NativeRuntime.Load(qualities[0].Talker, qualities[0].Codec, options.Backend,
            options.RuntimeDirectory, voiceDesign: false);
        using var q8 = NativeRuntime.Load(qualities[1].Talker, qualities[1].Codec, options.Backend,
            options.RuntimeDirectory, voiceDesign: false);
        var runtimes = new[] { (Quality: qualities[0], Runtime: q4), (Quality: qualities[1], Runtime: q8) };

        var variants = catalog.Domains.SelectMany(Variants).ToArray();
        var total = variants.Length * Languages.Length;
        var complete = 0;
        foreach (var variant in variants)
        foreach (var language in Languages)
        {
            if (runtimes.All(item => Exists(database, variant.Domain.Id, variant.Id, language, item.Quality.Hash)))
            {
                complete++;
                continue;
            }
            var instruction = BuildInstruction(variant, language);
            var text = ReferenceTexts[language];
            var seed = StableSeed($"fallback\0{catalog.Version}\0{variant.Domain.Id}\0{variant.Id}\0{language}");
            var audio = design.Synthesize(text, language, instruction, seed);
            foreach (var (quality, runtime) in runtimes)
            {
                if (Exists(database, variant.Domain.Id, variant.Id, language, quality.Hash)) continue;
                var designed = runtime.Extract(audio) with { Text = text };
                var final = ApplyAuthenticAnchor(database, variant, language, quality.Hash, designed);
                var profileHash = HashProfile(language, quality.Hash, final.Text, final.Embedding, final.Codes,
                    variant.Domain.Id, catalog.Version);
                Upsert(database, variant, language, quality.Hash, final, profileHash);
            }
            complete++;
            if (complete % 10 == 0 || complete == total)
                Console.WriteLine($"{complete}/{total} {variant.Domain.Id}/{variant.Id}/{language}");
        }
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
        command.ExecuteNonQuery();
        Console.WriteLine($"fallback_rows={Scalar(database, "SELECT COUNT(*) FROM fallback_profile")}");
        return 0;
    }

    private static IEnumerable<Variant> Variants(Domain domain) => domain.FallbackDimensions switch
    {
        "none" => [new(domain, "default", null, null)],
        "feminine_only" => [new(domain, "feminine", "feminine", null)],
        "sex" => [new(domain, "masculine", "masculine", null), new(domain, "feminine", "feminine", null)],
        "sex_age" =>
        [
            new(domain, "masculine_young", "masculine", "young"),
            new(domain, "masculine_adult", "masculine", "adult"),
            new(domain, "feminine_young", "feminine", "young"),
            new(domain, "feminine_adult", "feminine", "adult"),
        ],
        _ => throw new InvalidDataException($"Unknown fallback dimensions '{domain.FallbackDimensions}'"),
    };

    private static string BuildInstruction(Variant variant, string language)
    {
        var prompt = language == "english" ? variant.Domain.Prompts.English : variant.Domain.Prompts.Neutral;
        var parts = new List<string>
        {
            $"Create one reusable casting-reference voice for natural Final Fantasy XIV dialogue in {language}.",
            prompt,
            "Give the voice a stable, specific identity: natural breath support, clear consonants, nuanced sentence-level pitch movement, responsive conversational timing, and restrained emotional flexibility.",
        };
        if (variant.Sex == "masculine")
            parts.Add("Use a naturally masculine vocal register without exaggerated depth, chest resonance, or macho caricature.");
        else if (variant.Sex == "feminine")
            parts.Add("Use a naturally feminine vocal register without exaggerated pitch, breathiness, or fragility.");
        if (variant.Age == "young")
            parts.Add("Sound clearly young: lighter vocal weight, immediate energy, clean diction, and believable adolescence or early adulthood; never a childish caricature.");
        else if (variant.Age == "adult")
            parts.Add("Sound mature: settled vocal weight, confident breath support, nuanced timing, and controlled emotional responsiveness.");
        if (variant.Domain.FallbackDimensions is "none" or "sex" or "feminine_only")
            parts.Add("Do not impose an age stereotype; preserve the species' characteristic vocal identity.");
        parts.Add("Close-miked dry studio speech. No announcer delivery, whispering, shouting, singing, celebrity imitation, comic exaggeration, artificial reverb, or background sound.");
        return String.Join(' ', parts);
    }

    private static Reference ApplyAuthenticAnchor(SqliteConnection database, Variant variant, string language,
        string modelHash, Reference designed)
    {
        if (!AuthenticAnchors.TryGetValue(variant.Domain.Id, out var anchor)) return designed;
        // A generic token is safe for trait-independent domains. Sex-sensitive domains
        // require an explicitly matching actor token; otherwise keep the designed voice.
        if (variant.Domain.FallbackDimensions != "none"
            && !String.Equals(anchor.Sex, variant.Sex, StringComparison.Ordinal))
            return designed;
        using var command = database.CreateCommand();
        command.CommandText = """
            SELECT ref_text,speaker_embedding,rvq_codes,rvq_length,codebooks
            FROM official_profile WHERE group_id=$group AND language=$language AND model_hash=$model
            """;
        command.Parameters.AddWithValue("$group", anchor.GroupId);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$model", modelHash);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return designed;
        var authenticEmbedding = Floats((byte[])reader[1]);
        var rotated = Slerp(authenticEmbedding, designed.Embedding, 0.20f);
        var codes = Ints((byte[])reader[2]);
        var length = reader.GetInt32(3);
        var codebooks = reader.GetInt32(4);
        if (length > MaximumReferenceFrames)
        {
            var truncated = new int[MaximumReferenceFrames * codebooks];
            for (var codebook = 0; codebook < codebooks; codebook++)
                Array.Copy(codes, codebook * length, truncated,
                    codebook * MaximumReferenceFrames, MaximumReferenceFrames);
            codes = truncated;
            length = MaximumReferenceFrames;
        }
        return new(reader.GetString(0), rotated, codes, length, codebooks);
    }

    private static float[] Slerp(float[] from, float[] toward, float amount)
    {
        if (from.Length != toward.Length) throw new InvalidDataException("Embedding dimensions differ");
        double fromNorm = 0, towardNorm = 0, dot = 0;
        for (var i = 0; i < from.Length; i++)
        {
            fromNorm += from[i] * from[i];
            towardNorm += toward[i] * toward[i];
            dot += from[i] * toward[i];
        }
        fromNorm = Math.Sqrt(fromNorm); towardNorm = Math.Sqrt(towardNorm);
        var cosine = Math.Clamp(dot / (fromNorm * towardNorm), -1d, 1d);
        var angle = Math.Acos(cosine);
        if (angle < 1e-6) return from.Zip(toward, (a, b) => a + (b - a) * amount).ToArray();
        var denominator = Math.Sin(angle);
        var aWeight = Math.Sin((1 - amount) * angle) / denominator;
        var bWeight = Math.Sin(amount * angle) / denominator;
        var output = new float[from.Length];
        for (var i = 0; i < output.Length; i++)
            output[i] = (float)(aWeight * from[i] + bWeight * toward[i]);
        return output;
    }

    private static SqliteConnection OpenDatabase(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS fallback_profile(
              domain_id TEXT NOT NULL,variant_id TEXT NOT NULL,sex TEXT NULL,age TEXT NULL,
              language TEXT NOT NULL,model_hash TEXT NOT NULL,ref_text TEXT NOT NULL,
              speaker_embedding BLOB NOT NULL,rvq_codes BLOB NOT NULL,rvq_length INTEGER NOT NULL,
              codebooks INTEGER NOT NULL,profile_hash TEXT NOT NULL,
              PRIMARY KEY(domain_id,variant_id,language,model_hash));
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    private static void UpdatePackMetadata(SqliteConnection database, int catalogVersion, int packVersion)
    {
        using var command = database.CreateCommand();
        command.CommandText = "UPDATE pack_metadata SET pack_version=$pack,catalog_version=$catalog,created_utc=$utc";
        command.Parameters.AddWithValue("$pack", packVersion);
        command.Parameters.AddWithValue("$catalog", catalogVersion);
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Pack metadata row is missing or duplicated");
    }

    private static void RehashExistingFallbacks(SqliteConnection database, int catalogVersion)
    {
        using var read = database.CreateCommand();
        read.CommandText = "SELECT rowid,domain_id,language,model_hash,ref_text,speaker_embedding,rvq_codes,profile_hash FROM fallback_profile";
        using var reader = read.ExecuteReader();
        var updates = new List<(long RowId, string Hash)>();
        while (reader.Read())
        {
            var embedding = Floats((byte[])reader[5]);
            var codes = Ints((byte[])reader[6]);
            var expected = HashProfile(reader.GetString(2), reader.GetString(3), reader.GetString(4), embedding,
                codes, reader.GetString(1), catalogVersion);
            if (!String.Equals(expected, reader.GetString(7), StringComparison.OrdinalIgnoreCase))
                updates.Add((reader.GetInt64(0), expected));
        }
        reader.Close();
        foreach (var update in updates)
        {
            using var write = database.CreateCommand();
            write.CommandText = "UPDATE fallback_profile SET profile_hash=$hash WHERE rowid=$rowid";
            write.Parameters.AddWithValue("$hash", update.Hash); write.Parameters.AddWithValue("$rowid", update.RowId);
            write.ExecuteNonQuery();
        }
        if (updates.Count > 0) Console.WriteLine($"rehashed_existing={updates.Count}");
    }

    private static bool Exists(SqliteConnection database, string domain, string variant, string language, string model)
    {
        using var command = database.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM fallback_profile WHERE domain_id=$domain AND variant_id=$variant AND language=$language AND model_hash=$model)";
        command.Parameters.AddWithValue("$domain", domain); command.Parameters.AddWithValue("$variant", variant);
        command.Parameters.AddWithValue("$language", language); command.Parameters.AddWithValue("$model", model);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static void Upsert(SqliteConnection database, Variant variant, string language, string model,
        Reference reference, string hash)
    {
        using var command = database.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO fallback_profile VALUES(
              $domain,$variant,$sex,$age,$language,$model,$text,$embedding,$codes,$length,$codebooks,$hash)
            """;
        command.Parameters.AddWithValue("$domain", variant.Domain.Id);
        command.Parameters.AddWithValue("$variant", variant.Id);
        command.Parameters.AddWithValue("$sex", (object?)variant.Sex ?? DBNull.Value);
        command.Parameters.AddWithValue("$age", (object?)variant.Age ?? DBNull.Value);
        command.Parameters.AddWithValue("$language", language); command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$text", reference.Text);
        command.Parameters.AddWithValue("$embedding", Bytes(reference.Embedding));
        command.Parameters.AddWithValue("$codes", Bytes(reference.Codes));
        command.Parameters.AddWithValue("$length", reference.Length);
        command.Parameters.AddWithValue("$codebooks", reference.Codebooks); command.Parameters.AddWithValue("$hash", hash);
        command.ExecuteNonQuery();
    }

    private static string HashProfile(string language, string model, string text, float[] embedding, int[] codes,
        string domain, int catalogVersion)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"Designed\0{language}\0{model}\0\0\0\0{domain}\0{catalogVersion}\0{{}}\0{text}"));
        hash.AppendData(Bytes(embedding)); hash.AppendData(Bytes(codes));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static long StableSeed(string value) => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(value))) & Int64.MaxValue;
    private static byte[] Bytes<T>(T[] values) where T : struct => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static float[] Floats(byte[] bytes) { var values = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length); return values; }
    private static int[] Ints(byte[] bytes) { var values = new int[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length); return values; }
    private static object? Scalar(SqliteConnection database, string sql) { using var command = database.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true };

    private sealed record Options(string Catalog, string Database, string Models, string RuntimeDirectory,
        string VoiceDesignModel, string Backend, int PackVersion)
    {
        internal static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i += 2) values.Add(args[i], args[i + 1]);
            string Required(string key) => Path.GetFullPath(values.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing {key}"));
            var models = Required("--models");
            return new(Required("--catalog"), Required("--database"), models, Required("--runtime"),
                Path.Combine(models, "qwen-talker-1.7b-voicedesign-Q4_K_M.gguf"),
                values.GetValueOrDefault("--backend") ?? "CUDA0", Int32.Parse(values.GetValueOrDefault("--pack-version") ?? "3"));
        }
    }

    private static Quality[] LoadQualities(Options options)
    {
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(options.Catalog)!, "models.json")));
        string Hash(string id) => manifest.RootElement.GetProperty("artifacts").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id).GetProperty("sha256").GetString()!;
        return
        [
            new("q4", Path.Combine(options.Models, "qwen-talker-0.6b-base-Q4_K_M.gguf"), Path.Combine(options.Models, "qwen-tokenizer-12hz-Q4_K_M.gguf"), Hash("base-q4")),
            new("q8", Path.Combine(options.Models, "qwen-talker-0.6b-base-Q8_0.gguf"), Path.Combine(options.Models, "qwen-tokenizer-12hz-Q8_0.gguf"), Hash("base-q8")),
        ];
    }

    private sealed record Catalog(int Version, Domain[] Domains);
    private sealed record Domain(string Id, string FallbackDimensions, Prompts Prompts);
    private sealed record Prompts(string English, string Neutral);
    private sealed record Variant(Domain Domain, string Id, string? Sex, string? Age);
    private sealed record Quality(string Id, string Talker, string Codec, string Hash);
    private sealed record Anchor(string GroupId, string? Sex);
    private sealed record Reference(string Text, float[] Embedding, int[] Codes, int Length, int Codebooks);

    private sealed unsafe class NativeRuntime(nint context, bool voiceDesign) : IDisposable
    {
        internal static NativeRuntime Load(string talker, string codec, string backend, string runtime, bool voiceDesign)
        {
            Native.BackendLoadFromPath(runtime);
            using var talkerString = new Utf8(talker); using var codecString = new Utf8(codec); using var backendString = new Utf8(backend);
            Native.InitDefaultParams(out var parameters); parameters.TalkerPath = talkerString.Pointer;
            parameters.CodecPath = codecString.Pointer; parameters.BackendName = backendString.Pointer;
            var context = Native.Init(ref parameters);
            if (context == 0) throw new InvalidOperationException("qt_init failed: " + LastError());
            return new(context, voiceDesign);
        }
        internal Reference Extract(float[] samples)
        {
            Native.VoiceRef native = default;
            fixed (float* pointer = samples)
                if (Native.ExtractVoiceRef(context, pointer, samples.Length, out native) != 0)
                    throw new InvalidOperationException("qt_extract_voice_ref failed: " + LastError());
            try
            {
                var embedding = new float[native.SpeakerDimension]; var codes = new int[native.ReferenceLength * native.Codebooks];
                Marshal.Copy((nint)native.SpeakerEmbedding, embedding, 0, embedding.Length);
                Marshal.Copy((nint)native.Codes, codes, 0, codes.Length);
                return new(String.Empty, embedding, codes, native.ReferenceLength, native.Codebooks);
            }
            finally { Native.VoiceRefFree(ref native); }
        }
        internal float[] Synthesize(string text, string language, string instruction, long seed)
        {
            if (!voiceDesign) throw new InvalidOperationException("Not a VoiceDesign context");
            Native.TtsDefaultParams(out var parameters);
            using var textString = new Utf8(text); using var languageString = new Utf8(language); using var instructionString = new Utf8(instruction);
            parameters.Text = textString.Pointer; parameters.Language = languageString.Pointer;
            parameters.Instruction = instructionString.Pointer; parameters.Seed = seed;
            parameters.MaxNewTokens = 180;
            Native.Audio audio = default;
            var status = Native.Synthesize(context, ref parameters, out audio);
            if (status != 0) throw new InvalidOperationException($"qt_synthesize failed ({status}): {LastError()}");
            try { var samples = new float[audio.SampleCount]; Marshal.Copy((nint)audio.Samples, samples, 0, samples.Length); return samples; }
            finally { Native.AudioFree(ref audio); }
        }
        public void Dispose() => Native.Free(context);
        private static string LastError() => Marshal.PtrToStringUTF8((nint)Native.LastError()) ?? "unknown";
    }

    private sealed unsafe class Utf8 : IDisposable
    {
        private nint memory;
        internal byte* Pointer => (byte*)memory;
        internal Utf8(string value) { var bytes = Encoding.UTF8.GetBytes(value + '\0'); memory = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, memory, bytes.Length); }
        public void Dispose() { if (memory != 0) Marshal.FreeHGlobal(Interlocked.Exchange(ref memory, 0)); }
    }
}

internal static unsafe partial class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct InitParams { internal int AbiVersion; internal byte* TalkerPath; internal byte* CodecPath; internal byte UseFlashAttention; internal byte ClampFp16; internal int MaxBatch; internal float CodecChunkSeconds; internal byte* BackendName; }
    [StructLayout(LayoutKind.Sequential)] internal struct TtsParams { internal int AbiVersion; internal byte* Text; internal byte* Language; internal byte* Instruction; internal byte* Speaker; internal float* ReferenceAudio; internal int ReferenceSampleCount; internal byte* ReferenceText; internal long Seed; internal int MaxNewTokens; internal byte DoSample; internal float Temperature; internal int TopK; internal float TopP; internal float RepetitionPenalty; internal byte SubtalkerDoSample; internal float SubtalkerTemperature; internal int SubtalkerTopK; internal float SubtalkerTopP; internal byte* DumpDirectory; internal nint Cancel; internal void* CancelUserData; internal nint OnChunk; internal void* OnChunkUserData; internal float* ReferenceSpeakerEmbedding; internal int ReferenceSpeakerDimension; internal int* ReferenceCodes; internal int ReferenceLength; }
    [StructLayout(LayoutKind.Sequential)] internal struct Audio { internal float* Samples; internal int SampleCount; internal int SampleRate; internal int Channels; }
    [StructLayout(LayoutKind.Sequential)] internal struct VoiceRef { internal float* SpeakerEmbedding; internal int SpeakerDimension; internal int* Codes; internal int ReferenceLength; internal int Codebooks; }
    [LibraryImport("qwen", EntryPoint = "qt_backend_load_from_path", StringMarshalling = StringMarshalling.Utf8)] internal static partial int BackendLoadFromPath(string path);
    [LibraryImport("qwen", EntryPoint = "qt_init_default_params")] internal static partial void InitDefaultParams(out InitParams parameters);
    [LibraryImport("qwen", EntryPoint = "qt_tts_default_params")] internal static partial void TtsDefaultParams(out TtsParams parameters);
    [LibraryImport("qwen", EntryPoint = "qt_init")] internal static partial nint Init(ref InitParams parameters);
    [LibraryImport("qwen", EntryPoint = "qt_free")] internal static partial void Free(nint context);
    [LibraryImport("qwen", EntryPoint = "qt_synthesize")] internal static partial int Synthesize(nint context, ref TtsParams parameters, out Audio audio);
    [LibraryImport("qwen", EntryPoint = "qt_audio_free")] internal static partial void AudioFree(ref Audio audio);
    [LibraryImport("qwen", EntryPoint = "qt_extract_voice_ref")] internal static partial int ExtractVoiceRef(nint context, float* samples, int sampleCount, out VoiceRef voiceRef);
    [LibraryImport("qwen", EntryPoint = "qt_voice_ref_free")] internal static partial void VoiceRefFree(ref VoiceRef voiceRef);
    [LibraryImport("qwen", EntryPoint = "qt_last_error")] internal static partial byte* LastError();
}
