using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NAudio.Wave;
using Resonance.Tts;

return Generator.Run(args);

internal static class Generator
{
    private static readonly string[] Languages = ["english", "japanese", "german", "french"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static int Run(string[] args)
    {
        var options = Options.Parse(args);
        var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(options.Catalog), JsonOptions)
            ?? throw new InvalidDataException("Catalog is empty");
        var recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(options.Recipe), JsonOptions)
            ?? throw new InvalidDataException("Casting recipe is empty");
        if (!catalog.Domains.Select(d => d.Id).ToHashSet().SetEquals(recipe.Domains.Select(d => d.Id)))
            throw new InvalidDataException("Casting recipes must exactly cover the catalog domains");
        if (recipe.Foundations.Select(f => f.Id).Distinct().Count() != 6
            || recipe.Foundations.Any(f => f.Id != $"{f.Sex}_{f.Age}"
                || f.Sex is not ("masculine" or "feminine") || f.Age is not ("young" or "adult" or "elder")))
            throw new InvalidDataException("Six gender/age foundations are required");
        if (recipe.Foundations.Any(f => !double.IsFinite(f.NativeWeight) || f.NativeWeight is <= 0 or > 1)
            || recipe.Domains.Any(d => !double.IsFinite(d.Weight) || d.Weight is < 0 or > 1))
            throw new InvalidDataException("Native foundation weights must be >0 through 1; flavor weights must be 0 through 1");
        foreach (var domain in recipe.Domains.Where(d => d.NativeBlend is not null))
            if (domain.NativeBlend!.Count == 0 || domain.Weight != 0 || domain.Anchors.Count != 0
                || domain.Recording is not null
                || domain.NativeBlend.Any(pair => String.IsNullOrWhiteSpace(pair.Key)
                    || !double.IsFinite(pair.Value) || pair.Value is <= 0 or > 1)
                || domain.NativeBlend.Values.Sum() > 1)
                throw new InvalidDataException($"Invalid native blend or combined reference recipes: {domain.Id}");
        foreach (var domain in recipe.Domains.Where(d => d.Recording is not null))
            if (domain.Weight != 0 || domain.Anchors.Count != 0
                || String.IsNullOrWhiteSpace(domain.Recording!.Transcript)
                || String.IsNullOrWhiteSpace(domain.Recording.AudioFile)
                || !File.Exists(Path.Combine(options.References, domain.Recording.AudioFile)))
                throw new InvalidDataException($"Clone reference missing or combined with flavor blending: {domain.Id}");
        var qualities = LoadQualities(options);
        var variants = catalog.Domains.SelectMany(domain => recipe.Foundations
            .Where(f => domain.FallbackDimensions == "sex_age"
                || domain.FallbackDimensions == "feminine_only" && f.Sex == "feminine"
                || domain.FallbackDimensions == "none" && f.Id == "masculine_adult")
            .Select(f => new Variant(domain, recipe.Domains.Single(d => d.Id == domain.Id), f))).ToArray();
        if (variants.Length != catalog.Domains.Sum(d => d.FallbackDimensions switch
            { "sex_age" => 6, "feminine_only" => 3, "none" => 1, _ => throw new InvalidDataException("Unsupported casting dimensions") }))
            throw new InvalidDataException("Incomplete casting matrix");
        Directory.CreateDirectory(options.References);
        using var cache = Open(options.References + "/references.sqlite3");
        Execute(cache, """
            CREATE TABLE IF NOT EXISTS reference_audio(id TEXT PRIMARY KEY,prompt TEXT NOT NULL,
              seed INTEGER NOT NULL,ref_text TEXT NOT NULL,audio_file TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS reference_profile(id TEXT NOT NULL,model_hash TEXT NOT NULL,
              prompt TEXT NOT NULL,seed INTEGER NOT NULL,ref_text TEXT NOT NULL,speaker_embedding BLOB NOT NULL,
              rvq_codes BLOB NOT NULL,rvq_length INTEGER NOT NULL,codebooks INTEGER NOT NULL,
              PRIMARY KEY(id,model_hash));
            """);
        var designs = recipe.Foundations.Select(f => new Design("foundation:" + f.Id, f.Prompt, f.Seed))
            .Concat(variants.Where(v => v.Flavor.Weight > 0 && !v.Flavor.Anchors.ContainsKey(v.Foundation.Sex))
                .Select(v => new Design($"flavor:{v.Domain.Id}:{v.Foundation.Id}",
                    $"{v.Foundation.Prompt} {v.Flavor.Flavor} {recipe.FlavorSuffix}", v.Foundation.Seed)))
            .DistinctBy(d => d.Id).ToArray();
        using var database = Open(options.Database);
        var actors = recipe.Foundations.Select(f => f.NativeActor)
            .Concat(recipe.Domains.SelectMany(d => d.Anchors.Values))
            .Concat(recipe.Domains.SelectMany(d => d.NativeBlend?.Keys.AsEnumerable() ?? []))
            .Distinct().ToArray();
        foreach (var quality in qualities)
        foreach (var language in Languages)
        foreach (var actor in actors)
            NativeReference(database, null, actor, language, quality);
        EnsureAudio(options, recipe, cache, designs, qualities.SingleOrDefault(q => q.Id == "1.7b-q8") ?? qualities[0]);
        using var transaction = database.BeginTransaction();
        Execute(database, """
            CREATE TABLE IF NOT EXISTS fallback_profile(
              domain_id TEXT NOT NULL,variant_id TEXT NOT NULL,sex TEXT,age TEXT,
              language TEXT NOT NULL,model_hash TEXT NOT NULL,ref_text TEXT NOT NULL,
              speaker_embedding BLOB NOT NULL,rvq_codes BLOB NOT NULL,rvq_length INTEGER NOT NULL,
              codebooks INTEGER NOT NULL,profile_hash TEXT NOT NULL,
              PRIMARY KEY(domain_id,variant_id,language,model_hash));
            """, transaction);
        Execute(database, "DELETE FROM fallback_profile", transaction);
        foreach (var quality in qualities)
        {
            NativeRuntime? runtime = null;
            try
            {
                Reference Designed(string id)
                {
                    var spec = designs.Single(d => d.Id == id);
                    using var query = cache.CreateCommand();
                    query.CommandText = "SELECT ref_text,speaker_embedding,rvq_codes,rvq_length,codebooks FROM reference_profile WHERE id=$id AND model_hash=$model AND prompt=$prompt AND seed=$seed";
                    query.Parameters.AddWithValue("$id", id); query.Parameters.AddWithValue("$model", quality.Hash);
                    query.Parameters.AddWithValue("$prompt", spec.Prompt); query.Parameters.AddWithValue("$seed", spec.Seed);
                    using (var reader = query.ExecuteReader())
                        if (reader.Read()) return ReadReference(reader, quality.Dimension);
                    using var audio = cache.CreateCommand();
                    audio.CommandText = "SELECT ref_text,audio_file FROM reference_audio WHERE id=$id";
                    audio.Parameters.AddWithValue("$id", id);
                    string text, file;
                    using (var reader = audio.ExecuteReader())
                    {
                        if (!reader.Read()) throw new InvalidDataException($"Reference audio missing: {id}");
                        text = reader.GetString(0); file = reader.GetString(1);
                    }
                    runtime ??= NativeRuntime.Load(quality.Talker, quality.Codec, options.Backend, options.Runtime);
                    var reference = runtime.Extract(ReadAudio(Path.Combine(options.References, file))) with { Text = text };
                    Validate(reference, quality.Dimension);
                    if (reference.Length >= 220) throw new InvalidDataException($"Designed reference reached its generation limit: {id}");
                    using var save = cache.CreateCommand();
                    save.CommandText = "INSERT OR REPLACE INTO reference_profile VALUES($id,$model,$prompt,$seed,$text,$embedding,$codes,$length,$books)";
                    save.Parameters.AddWithValue("$id", id); save.Parameters.AddWithValue("$model", quality.Hash);
                    save.Parameters.AddWithValue("$prompt", spec.Prompt); save.Parameters.AddWithValue("$seed", spec.Seed);
                    AddReference(save, reference); save.ExecuteNonQuery();
                    return reference;
                }
                var generic = recipe.Foundations.ToDictionary(f => f.Id, f => Designed("foundation:" + f.Id));
                var clones = new Dictionary<string, Reference>(StringComparer.Ordinal);
                foreach (var flavor in recipe.Domains.Where(d => d.Recording is not null))
                {
                    runtime ??= NativeRuntime.Load(quality.Talker, quality.Codec, options.Backend, options.Runtime);
                    var clone = runtime.Extract(ReadAudio(Path.Combine(options.References, flavor.Recording!.AudioFile)))
                        with { Text = flavor.Recording.Transcript };
                    Validate(clone, quality.Dimension);
                    clones.Add(flavor.Id, clone);
                }
                var total = 0;
                foreach (var language in Languages)
                {
                    var ageReferences = recipe.Foundations.ToDictionary(f => f.Id,
                        f => NativeReference(database, transaction, f.NativeActor, language, quality));
                    var bases = recipe.Foundations.ToDictionary(f => f.Id,
                        f => VoiceEmbedding.LinearBlend(generic[f.Id].Embedding,
                            ageReferences[f.Id].Embedding, f.NativeWeight));
                    foreach (var variant in variants)
                    {
                        var foundation = variant.Foundation;
                        var embedding = bases[foundation.Id];
                        var performance = ageReferences[foundation.Id];
                        if (variant.Flavor.Weight > 0)
                        {
                            var flavor = variant.Flavor.Anchors.TryGetValue(foundation.Sex, out var actor)
                                ? NativeReference(database, transaction, actor, language, quality)
                                : Designed($"flavor:{variant.Domain.Id}:{foundation.Id}");
                            embedding = VoiceEmbedding.LinearBlend(embedding, flavor.Embedding, variant.Flavor.Weight);
                        }
                        if (variant.Flavor.NativeBlend is { } blend)
                        {
                            var mass = 1 - blend.Values.Sum();
                            foreach (var (actor, weight) in blend)
                            {
                                mass += weight;
                                var anchor = NativeReference(database, transaction, actor, language, quality);
                                embedding = VoiceEmbedding.LinearBlend(embedding, anchor.Embedding, weight / mass);
                            }
                            performance = NativeReference(database, transaction,
                                blend.MaxBy(pair => pair.Value).Key, language, quality);
                        }
                        var reference = clones.TryGetValue(variant.Domain.Id, out var clone)
                            ? clone : performance with { Embedding = embedding };
                        using var insert = database.CreateCommand(); insert.Transaction = transaction;
                        insert.CommandText = "INSERT INTO fallback_profile VALUES($domain,$variant,$sex,$age,$language,$model,$text,$embedding,$codes,$length,$books,$hash)";
                        insert.Parameters.AddWithValue("$domain", variant.Domain.Id);
                        var shared = variant.Domain.FallbackDimensions == "none";
                        insert.Parameters.AddWithValue("$variant", shared ? "default" : foundation.Id);
                        insert.Parameters.AddWithValue("$sex", shared ? DBNull.Value : foundation.Sex);
                        insert.Parameters.AddWithValue("$age", shared ? DBNull.Value : foundation.Age);
                        insert.Parameters.AddWithValue("$language", language); insert.Parameters.AddWithValue("$model", quality.Hash);
                        AddReference(insert, reference);
                        insert.Parameters.AddWithValue("$hash", HashProfile(language, quality.Hash, reference, variant.Domain.Id, catalog.Version));
                        insert.ExecuteNonQuery(); total++;
                    }
                }
                Console.WriteLine($"[fallback] {quality.Id}: {total} profiles; native conditioning retained");
            }
            finally { runtime?.Dispose(); }
        }
        using (var metadata = database.CreateCommand())
        {
            metadata.Transaction = transaction;
            metadata.CommandText = "UPDATE pack_metadata SET pack_version=$pack,catalog_version=$catalog,created_utc=$utc";
            metadata.Parameters.AddWithValue("$pack", options.PackVersion); metadata.Parameters.AddWithValue("$catalog", catalog.Version);
            metadata.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            if (metadata.ExecuteNonQuery() != 1) throw new InvalidDataException("Pack metadata row missing or duplicated");
        }
        transaction.Commit();
        Execute(database, "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;");
        Console.WriteLine($"[fallback] complete: {variants.Length * Languages.Length * qualities.Length} profiles, catalog {catalog.Version}, pack {options.PackVersion}");
        return 0;
    }

    private static void EnsureAudio(Options options, Recipe recipe, SqliteConnection cache, Design[] designs, Quality quality)
    {
        NativeRuntime? runtime = null;
        try
        {
            foreach (var design in designs)
            {
                using var query = cache.CreateCommand();
                query.CommandText = "SELECT audio_file FROM reference_audio WHERE id=$id AND prompt=$prompt AND seed=$seed";
                query.Parameters.AddWithValue("$id", design.Id); query.Parameters.AddWithValue("$prompt", design.Prompt);
                query.Parameters.AddWithValue("$seed", design.Seed);
                if (query.ExecuteScalar() is string existing && File.Exists(Path.Combine(options.References, existing))) continue;
                if (!File.Exists(options.VoiceDesign))
                    throw new FileNotFoundException("Reference generation requires the Q8 VoiceDesign model", options.VoiceDesign);
                runtime ??= NativeRuntime.Load(options.VoiceDesign, quality.Codec, options.Backend, options.Runtime);
                var texts = new[] { recipe.ReferenceText, "Wait! Sit down. Tell me what happened." }.Distinct();
                var complete = false;
                foreach (var text in texts)
                {
                    var samples = runtime.Synthesize(text, design.Prompt, design.Seed);
                    if (samples.Length >= 220 * 1920) continue;
                    var name = design.Id.Replace(':', '_') + ".wav";
                    var file = Path.Combine(options.References, name);
                    using (var writer = new WaveFileWriter(file + ".partial", WaveFormat.CreateIeeeFloatWaveFormat(24000, 1)))
                        writer.WriteSamples(samples, 0, samples.Length);
                    File.Move(file + ".partial", file, true);
                    using var replacement = cache.BeginTransaction();
                    using var save = cache.CreateCommand(); save.Transaction = replacement;
                    save.CommandText = "DELETE FROM reference_profile WHERE id=$id; INSERT OR REPLACE INTO reference_audio VALUES($id,$prompt,$seed,$text,$file)";
                    save.Parameters.AddWithValue("$id", design.Id); save.Parameters.AddWithValue("$prompt", design.Prompt);
                    save.Parameters.AddWithValue("$seed", design.Seed); save.Parameters.AddWithValue("$text", text);
                    save.Parameters.AddWithValue("$file", name); save.ExecuteNonQuery(); replacement.Commit();
                    Console.WriteLine($"[fallback] designed {design.Id}"); complete = true; break;
                }
                if (!complete) throw new InvalidDataException($"Reference generation did not finish within 220 frames: {design.Id}");
            }
        }
        finally { runtime?.Dispose(); }
    }

    private static float[] ReadAudio(string path)
    {
        using var reader = new WaveFileReader(path);
        if (reader.WaveFormat.SampleRate != 24000 || reader.WaveFormat.Channels != 1)
            throw new InvalidDataException($"Reference must be 24 kHz mono: {path}");
        var samples = new float[reader.Length / reader.WaveFormat.BlockAlign];
        var provider = reader.ToSampleProvider(); var offset = 0;
        while (offset < samples.Length)
        {
            var read = provider.Read(samples, offset, samples.Length - offset);
            if (read == 0) throw new EndOfStreamException(path);
            offset += read;
        }
        return samples;
    }

    private static Reference NativeReference(SqliteConnection database, SqliteTransaction? transaction,
        string actor, string language, Quality quality)
    {
        using var query = database.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT ref_text,speaker_embedding,rvq_codes,rvq_length,codebooks FROM official_profile WHERE group_id=$group AND language=$language AND model_hash=$model";
        query.Parameters.AddWithValue("$group", actor); query.Parameters.AddWithValue("$language", language);
        query.Parameters.AddWithValue("$model", quality.Hash);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException($"Missing declared native anchor: {actor}/{language}/{quality.Id}");
        return ReadReference(reader, quality.Dimension);
    }

    private static Reference ReadReference(SqliteDataReader reader, int dimension)
    {
        var reference = new Reference(reader.GetString(0), Floats((byte[])reader[1]), Ints((byte[])reader[2]), reader.GetInt32(3), reader.GetInt32(4));
        Validate(reference, dimension); return reference;
    }
    private static void Validate(Reference reference, int dimension)
    {
        if (String.IsNullOrWhiteSpace(reference.Text) || reference.Embedding.Length != dimension
            || reference.Embedding.Any(v => !float.IsFinite(v)) || reference.Codebooks != 16
            || reference.Length is < 1 or > 250 || reference.Codes.Length != reference.Length * 16
            || reference.Codes.Any(v => v is < 0 or >= 2048))
            throw new InvalidDataException("Invalid reference shape, transcript or latent values");
    }
    private static void AddReference(SqliteCommand command, Reference reference)
    {
        command.Parameters.AddWithValue("$text", reference.Text); command.Parameters.AddWithValue("$embedding", Bytes(reference.Embedding));
        command.Parameters.AddWithValue("$codes", Bytes(reference.Codes)); command.Parameters.AddWithValue("$length", reference.Length);
        command.Parameters.AddWithValue("$books", reference.Codebooks);
    }
    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={Path.GetFullPath(path)}"); connection.Open(); return connection;
    }
    private static void Execute(SqliteConnection database, string sql, SqliteTransaction? transaction = null)
    {
        using var command = database.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static string HashProfile(string language, string model, Reference reference, string domain, int version)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"Designed\0{language}\0{model}\0\0\0\0{domain}\0{version}\0{{}}\0{reference.Text}"));
        hash.AppendData(Bytes(reference.Embedding)); hash.AppendData(Bytes(reference.Codes));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static byte[] Bytes<T>(T[] values) where T : struct => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    private static float[] Floats(byte[] bytes) { var values = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length); return values; }
    private static int[] Ints(byte[] bytes) { var values = new int[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length); return values; }
    private static Quality[] LoadQualities(Options options)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(options.Catalog)!, "models.json")));
        JsonElement Artifact(string id) => manifest.RootElement.GetProperty("artifacts").EnumerateArray().Single(a => a.GetProperty("id").GetString() == id);
        return options.Qualities.Select(id =>
        {
            if (id is not ("q4" or "q8" or "1.7b-q4" or "1.7b-q8")) throw new ArgumentException($"Unknown quality: {id}");
            var talker = Artifact("base-" + id); var codec = Artifact("tokenizer-" + (id.EndsWith("q8", StringComparison.Ordinal) ? "q8" : "q4"));
            return new Quality(id,Path.Combine(options.Models,talker.GetProperty("fileName").GetString()!),
                Path.Combine(options.Models,codec.GetProperty("fileName").GetString()!),talker.GetProperty("sha256").GetString()!,id.StartsWith("1.7b",StringComparison.Ordinal) ? 2048 : 1024);
        }).ToArray();
    }
    private sealed record Options(string Catalog,string Recipe,string Database,string Models,string Runtime,string References,string VoiceDesign,string[] Qualities,string Backend,int PackVersion)
    {
        internal static Options Parse(string[] args)
        {
            if (args.Length % 2 != 0) throw new ArgumentException("Options require --name value pairs");
            var values = Enumerable.Range(0,args.Length/2).ToDictionary(i=>args[i*2],i=>args[i*2+1],StringComparer.Ordinal);
            string Required(string name) => Path.GetFullPath(values.TryGetValue(name,out var value) ? value : throw new ArgumentException($"Missing {name}"));
            var catalog = Required("--catalog"); var models = Required("--models");
            return new(catalog,Path.GetFullPath(values.GetValueOrDefault("--recipe") ?? Path.Combine(Path.GetDirectoryName(catalog)!,"fallback-voices.json")),
                Required("--database"),models,Required("--runtime"),
                Path.GetFullPath(values.GetValueOrDefault("--references") ?? ".references/artifacts/fallback-references"),
                Path.Combine(models,"qwen-talker-1.7b-voicedesign-Q8_0.gguf"),
                (values.GetValueOrDefault("--qualities") ?? "q4,q8,1.7b-q4,1.7b-q8").Split(','),values.GetValueOrDefault("--backend") ?? "CUDA0",
                Int32.Parse(values.GetValueOrDefault("--pack-version") ?? "9"));
        }
    }
    private sealed record Catalog(int Version, Domain[] Domains);
    private sealed record Domain(string Id,string FallbackDimensions);
    private sealed record Recipe(int Version,string ReferenceText,string FlavorSuffix,Foundation[] Foundations,FlavorRecipe[] Domains);
    private sealed record Foundation(string Id,string Sex,string Age,string NativeActor,string Prompt,long Seed,double NativeWeight);
    private sealed record FlavorRecipe(string Id,string Flavor,Dictionary<string,string> Anchors,double Weight,string[] Sources,CloneReference? Recording = null,Dictionary<string,double>? NativeBlend = null);
    private sealed record CloneReference(string AudioFile,string Transcript);
    private sealed record Variant(Domain Domain,FlavorRecipe Flavor,Foundation Foundation);
    private sealed record Design(string Id,string Prompt,long Seed);
    private sealed record Quality(string Id,string Talker,string Codec,string Hash,int Dimension);
    internal sealed record Reference(string Text,float[] Embedding,int[] Codes,int Length,int Codebooks);
}
