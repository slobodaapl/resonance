using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumina;
using Microsoft.Data.Sqlite;
using Resonance.Game;
using Resonance.Tts;

return await PackBuilder.RunAsync(args);

internal static class PackBuilder
{
    private const int LegacySchemaVersion = 1;
    private const int GraphSchemaVersion = 2;
    private const int BoundarySilenceSamples = 3600;

    internal static async Task<int> RunAsync(string[] args)
    {
        var options = Options.Parse(args);
        var inputJson = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var selection = JsonSerializer.Deserialize<Selection>(
                            await File.ReadAllTextAsync(options.Selection), inputJson)
                        ?? throw new InvalidDataException("Selected source document is empty");
        if (selection.SchemaVersion < 2
            || !String.Equals(selection.Eligibility, "undubbed-occurrence", StringComparison.Ordinal))
            throw new InvalidDataException(
                "Selection was not filtered to actors with undubbed occurrences");
        var catalog = JsonSerializer.Deserialize<Catalog>(await File.ReadAllTextAsync(options.Catalog), inputJson)
                      ?? throw new InvalidDataException("Official catalog is empty");
        var groups = catalog.Groups
            .SelectMany(group => group.ActorTokens.Select(token => (Token: NormalizeToken(token), Group: group)))
            .ToDictionary(value => value.Token, value => value.Group, StringComparer.Ordinal);
        var models = JsonSerializer.Deserialize<ModelManifest>(
                         await File.ReadAllTextAsync(options.ModelsManifest), inputJson)
                     ?? throw new InvalidDataException("Model manifest is empty");
        var qualities = options.Qualities.Select(quality => ResolveQuality(models, options.ModelsDirectory, quality))
            .ToArray();
        var scenePlan = options.ScenePlan is null
            ? null
            : JsonSerializer.Deserialize<ScenePlan>(await File.ReadAllTextAsync(options.ScenePlan), inputJson)
              ?? throw new InvalidDataException("Scene plan document is empty");
        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputDatabase)!);
        using var database = OpenDatabase(options.OutputDatabase, options.PackVersion, catalog.CatalogVersion,
            scenePlan is null ? LegacySchemaVersion : GraphSchemaVersion);
        if (scenePlan is not null) ReplaceScenePlan(database, scenePlan);
        PruneIneligibleProfiles(database, selection, groups);
        var game = new GameData(options.SqpackDirectory);

        foreach (var quality in qualities)
        {
            Console.WriteLine($"loading {quality.Id}: {quality.TalkerPath}");
            var pending = selection.Actors.Sum(actor => actor.Languages.Count(pair =>
            {
                if (!groups.TryGetValue(NormalizeToken(actor.ActorToken), out var candidateGroup))
                    throw new InvalidDataException($"Catalog has no exact actor token '{actor.ActorToken}'");
                return !ProfileExists(database, candidateGroup.Id, pair.Key, quality.ModelHash);
            }));
            if (pending == 0)
            {
                Console.WriteLine($"{quality.Id}: all profiles already present");
                continue;
            }
            using var runtime = NativeReferenceRuntime.Load(
                quality.TalkerPath, quality.CodecPath, options.Backend, options.RuntimeDirectory);
            var completed = 0;
            var expected = selection.Actors.Sum(actor => actor.Languages.Count);
            foreach (var actor in selection.Actors)
            {
                if (!groups.TryGetValue(NormalizeToken(actor.ActorToken), out var group))
                    throw new InvalidDataException($"Catalog has no exact actor token '{actor.ActorToken}'");
                foreach (var (language, sources) in actor.Languages)
                {
                    if (ProfileExists(database, group.Id, language, quality.ModelHash))
                    {
                        completed++;
                        continue;
                    }
                    var samples = new List<float>();
                    var transcripts = new List<string>();
                    foreach (var source in sources)
                    {
                        var resource = game.GetFile(source.ScdPath)
                                       ?? throw new FileNotFoundException("Selected SCD resource is unavailable",
                                           source.ScdPath);
                        if (samples.Count > 0) samples.AddRange(new float[BoundarySilenceSamples]);
                        samples.AddRange(TrimSilence(ScdAudioDecoder.Extract(
                            resource.Data, source.SoundNumber, CancellationToken.None)));
                        transcripts.Add(source.Transcript);
                    }
                    var duration = samples.Count / 24000d;
                    if (duration is <= 0 or >= 20)
                        throw new InvalidDataException(
                            $"Selected package {actor.ActorToken}/{language} is {duration:F3}s; expected >0–<20s");
                    var transcript = String.Join(' ', transcripts);
                    var reference = runtime.Extract(samples.ToArray());
                    if (reference.Embedding.Length != 1024 || reference.Codebooks != 16)
                        throw new InvalidDataException("Native reference shape is incompatible with the pack schema");
                    var profileHash = HashProfile(language, quality.ModelHash, transcript,
                        reference.Embedding, reference.Codes);
                    InsertProfile(database, group, actor.ActorToken, language, quality.ModelHash,
                        transcript, reference, profileHash, "{}");
                    completed++;
                    if (completed % 25 == 0 || completed == expected)
                        Console.WriteLine($"{quality.Id}: {completed}/{expected}");
                }
            }
        }

        var profileCount = Convert.ToInt32(Scalar(database, "SELECT COUNT(*) FROM official_profile"));
        using (var checkpoint = database.CreateCommand())
        {
            checkpoint.CommandText = """
                UPDATE official_profile SET source_metadata='{}' WHERE source_metadata <> '{}';
                PRAGMA wal_checkpoint(TRUNCATE);
                PRAGMA journal_mode=DELETE;
                """;
            checkpoint.ExecuteNonQuery();
        }
        Console.WriteLine($"profiles={profileCount} database={options.OutputDatabase}");
        return 0;
    }

    private static Quality ResolveQuality(ModelManifest manifest, string directory, string quality)
    {
        var baseModel = manifest.Artifacts.Single(value => value.Id == $"base-{quality}");
        var tokenizer = manifest.Artifacts.Single(value => value.Id == $"tokenizer-{quality}");
        var talker = Path.Combine(directory, baseModel.FileName);
        var codec = Path.Combine(directory, tokenizer.FileName);
        if (!File.Exists(talker)) throw new FileNotFoundException($"{quality} Base model is missing", talker);
        if (!File.Exists(codec)) throw new FileNotFoundException($"{quality} tokenizer is missing", codec);
        return new(quality, talker, codec, baseModel.Sha256);
    }

    private static SqliteConnection OpenDatabase(
        string path, int packVersion, int catalogVersion, int schemaVersion)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS pack_metadata(
              schema_version INTEGER NOT NULL,
              pack_version INTEGER NOT NULL,
              catalog_version INTEGER NOT NULL,
              created_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS official_profile(
              group_id TEXT NOT NULL,
              label TEXT NOT NULL,
              actor_token TEXT NOT NULL,
              language TEXT NOT NULL,
              model_hash TEXT NOT NULL,
              ref_text TEXT NOT NULL,
              speaker_embedding BLOB NOT NULL,
              rvq_codes BLOB NOT NULL,
              rvq_length INTEGER NOT NULL,
              codebooks INTEGER NOT NULL,
              source_metadata TEXT NOT NULL,
              profile_hash TEXT NOT NULL,
              PRIMARY KEY(group_id,language,model_hash));
            CREATE TABLE IF NOT EXISTS scene_node(
              cutscene_id INTEGER NOT NULL,
              language TEXT NOT NULL,
              node_id TEXT NOT NULL,
              sheet_key_hash TEXT NOT NULL,
              occurrence INTEGER NOT NULL,
              actor_npc_base_id INTEGER,
              official_group_id TEXT,
              actor_token_hash TEXT,
              node_kind TEXT NOT NULL CHECK(node_kind IN ('native','synthetic','choice')),
              PRIMARY KEY(cutscene_id,language,node_id));
            CREATE TABLE IF NOT EXISTS scene_edge(
              cutscene_id INTEGER NOT NULL,
              language TEXT NOT NULL,
              current_node_id TEXT,
              next_node_id TEXT,
              CHECK(current_node_id IS NOT NULL OR next_node_id IS NOT NULL));
            CREATE UNIQUE INDEX IF NOT EXISTS scene_edge_unique
              ON scene_edge(cutscene_id,language,IFNULL(current_node_id,''),IFNULL(next_node_id,''));
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='official_profile'";
        var profileSchema = Convert.ToString(command.ExecuteScalar()) ?? String.Empty;
        if (profileSchema.Contains("UNIQUE(profile_hash)", StringComparison.OrdinalIgnoreCase))
        {
            command.CommandText = """
                BEGIN IMMEDIATE;
                ALTER TABLE official_profile RENAME TO official_profile_unique_hash;
                CREATE TABLE official_profile(
                  group_id TEXT NOT NULL,label TEXT NOT NULL,actor_token TEXT NOT NULL,
                  language TEXT NOT NULL,model_hash TEXT NOT NULL,ref_text TEXT NOT NULL,
                  speaker_embedding BLOB NOT NULL,rvq_codes BLOB NOT NULL,
                  rvq_length INTEGER NOT NULL,codebooks INTEGER NOT NULL,
                  source_metadata TEXT NOT NULL,profile_hash TEXT NOT NULL,
                  PRIMARY KEY(group_id,language,model_hash));
                INSERT INTO official_profile SELECT * FROM official_profile_unique_hash;
                DROP TABLE official_profile_unique_hash;
                COMMIT;
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText = "SELECT schema_version FROM pack_metadata LIMIT 1";
        var schema = command.ExecuteScalar();
        if (schema is not null && Convert.ToInt32(schema) is not (LegacySchemaVersion or GraphSchemaVersion))
            throw new InvalidDataException("Existing pack database schema is incompatible");
        if (schema is not null && Convert.ToInt32(schema) == GraphSchemaVersion
            && schemaVersion == LegacySchemaVersion)
            schemaVersion = GraphSchemaVersion;
        command.CommandText = "DELETE FROM pack_metadata; INSERT INTO pack_metadata VALUES($schema,$pack,$catalog,$utc);";
        command.Parameters.AddWithValue("$schema", schemaVersion);
        command.Parameters.AddWithValue("$pack", packVersion);
        command.Parameters.AddWithValue("$catalog", catalogVersion);
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        return connection;
    }

    private static void ReplaceScenePlan(SqliteConnection database, ScenePlan plan)
    {
        if (plan.SchemaVersion != GraphSchemaVersion || plan.Nodes.Count == 0)
            throw new InvalidDataException("Scene plan must be a non-empty schema-v2 document");
        var nodeKeys = new HashSet<(uint CutsceneId, string Language, string NodeId)>();
        foreach (var node in plan.Nodes)
        {
            if (node.CutsceneId == 0 || String.IsNullOrWhiteSpace(node.Language)
                || String.IsNullOrWhiteSpace(node.NodeId) || !IsSha256(node.SheetKeyHash)
                || node.Occurrence < 0 || node.NodeKind is not ("native" or "synthetic" or "choice")
                || !nodeKeys.Add((node.CutsceneId, node.Language, node.NodeId)))
                throw new InvalidDataException("Scene plan contains an invalid or duplicate node");
            if (node.ActorTokenHash is not null && !IsSha256(node.ActorTokenHash))
                throw new InvalidDataException("Scene plan contains an invalid actor-token hash");
        }
        var edgeKeys = new HashSet<(uint CutsceneId, string Language, string? Current, string? Next)>();
        foreach (var edge in plan.Edges)
        {
            if (edge.CutsceneId == 0 || String.IsNullOrWhiteSpace(edge.Language)
                || edge.CurrentNodeId is null && edge.NextNodeId is null
                || !edgeKeys.Add((edge.CutsceneId, edge.Language, edge.CurrentNodeId, edge.NextNodeId))
                || edge.CurrentNodeId is not null
                   && !nodeKeys.Contains((edge.CutsceneId, edge.Language, edge.CurrentNodeId))
                || edge.NextNodeId is not null
                   && !nodeKeys.Contains((edge.CutsceneId, edge.Language, edge.NextNodeId)))
                throw new InvalidDataException("Scene plan contains an invalid, duplicate, or dangling edge");
        }
        foreach (var graph in nodeKeys.Select(value => (value.CutsceneId, value.Language)).Distinct())
        {
            if (!plan.Edges.Any(edge => edge.CutsceneId == graph.CutsceneId
                                       && edge.Language == graph.Language && edge.CurrentNodeId is null)
                || !plan.Edges.Any(edge => edge.CutsceneId == graph.CutsceneId
                                           && edge.Language == graph.Language && edge.NextNodeId is null))
                throw new InvalidDataException("Every scene graph requires explicit start and end edges");
        }
        using var transaction = database.BeginTransaction();
        Execute(database, transaction, "DELETE FROM scene_edge; DELETE FROM scene_node;");
        foreach (var node in plan.Nodes)
        {
            using var command = database.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO scene_node VALUES(
                  $cutscene,$language,$node,$sheet,$occurrence,$npc,$group,$actor,$kind)
                """;
            command.Parameters.AddWithValue("$cutscene", node.CutsceneId);
            command.Parameters.AddWithValue("$language", node.Language);
            command.Parameters.AddWithValue("$node", node.NodeId);
            command.Parameters.AddWithValue("$sheet", node.SheetKeyHash);
            command.Parameters.AddWithValue("$occurrence", node.Occurrence);
            command.Parameters.AddWithValue("$npc", (object?)node.ActorNpcBaseId ?? DBNull.Value);
            command.Parameters.AddWithValue("$group", (object?)node.OfficialGroupId ?? DBNull.Value);
            command.Parameters.AddWithValue("$actor", (object?)node.ActorTokenHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$kind", node.NodeKind);
            command.ExecuteNonQuery();
        }
        foreach (var edge in plan.Edges)
        {
            using var command = database.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO scene_edge VALUES($cutscene,$language,$current,$next)";
            command.Parameters.AddWithValue("$cutscene", edge.CutsceneId);
            command.Parameters.AddWithValue("$language", edge.Language);
            command.Parameters.AddWithValue("$current", (object?)edge.CurrentNodeId ?? DBNull.Value);
            command.Parameters.AddWithValue("$next", (object?)edge.NextNodeId ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void Execute(SqliteConnection database, SqliteTransaction transaction, string sql)
    {
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void PruneIneligibleProfiles(SqliteConnection database, Selection selection,
        IReadOnlyDictionary<string, CatalogGroup> groups)
    {
        var retainedGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var actor in selection.Actors)
        {
            if (!groups.TryGetValue(NormalizeToken(actor.ActorToken), out var group))
                throw new InvalidDataException($"Catalog has no exact actor token '{actor.ActorToken}'");
            retainedGroups.Add(group.Id);
        }

        using var transaction = database.BeginTransaction();
        using var create = database.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = "CREATE TEMP TABLE retained_official_group(group_id TEXT PRIMARY KEY)";
        create.ExecuteNonQuery();
        using var insert = database.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO retained_official_group(group_id) VALUES($group)";
        var parameter = insert.Parameters.Add("$group", SqliteType.Text);
        foreach (var groupId in retainedGroups)
        {
            parameter.Value = groupId;
            insert.ExecuteNonQuery();
        }
        using var delete = database.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM official_profile
            WHERE group_id NOT IN (SELECT group_id FROM retained_official_group)
            """;
        delete.ExecuteNonQuery();
        transaction.Commit();
    }

    private static bool ProfileExists(SqliteConnection database, string groupId, string language, string modelHash)
    {
        using var command = database.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM official_profile WHERE group_id=$group AND language=$language AND model_hash=$model)";
        command.Parameters.AddWithValue("$group", groupId);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$model", modelHash);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static void InsertProfile(SqliteConnection database, CatalogGroup group, string actorToken,
        string language, string modelHash, string transcript, NativeReference reference,
        string profileHash, string sourceMetadata)
    {
        using var command = database.CreateCommand();
        command.CommandText = """
            INSERT INTO official_profile(
              group_id,label,actor_token,language,model_hash,ref_text,speaker_embedding,
              rvq_codes,rvq_length,codebooks,source_metadata,profile_hash)
            VALUES($group,$label,$actor,$language,$model,$text,$embedding,$codes,$length,
                   $codebooks,$source,$hash)
            """;
        command.Parameters.AddWithValue("$group", group.Id);
        command.Parameters.AddWithValue("$label", group.Label);
        command.Parameters.AddWithValue("$actor", actorToken);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$model", modelHash);
        command.Parameters.AddWithValue("$text", transcript);
        command.Parameters.AddWithValue("$embedding", Bytes(reference.Embedding));
        command.Parameters.AddWithValue("$codes", Bytes(reference.Codes));
        command.Parameters.AddWithValue("$length", reference.Length);
        command.Parameters.AddWithValue("$codebooks", reference.Codebooks);
        command.Parameters.AddWithValue("$source", sourceMetadata);
        command.Parameters.AddWithValue("$hash", profileHash);
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            throw new InvalidDataException(
                $"Pack key collision for {group.Id}/{language}/{modelHash} from actor {actorToken}", error);
        }
    }

    private static string HashProfile(string language, string modelHash, string transcript,
        float[] embedding, int[] codes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(
            $"Official\0{language}\0{modelHash}\0\0\0\0\0\0\0{transcript}"));
        hash.AppendData(MemoryMarshal.AsBytes(embedding.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(codes.AsSpan()));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static byte[] Bytes<T>(T[] values) where T : struct =>
        MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    private static float[] TrimSilence(float[] samples)
    {
        const int frame = 480;
        if (samples.Length <= frame) return samples;
        var rms = new List<double>();
        for (var offset = 0; offset < samples.Length; offset += frame)
        {
            var count = Math.Min(frame, samples.Length - offset);
            var energy = 0d;
            for (var index = 0; index < count; index++)
                energy += samples[offset + index] * samples[offset + index];
            rms.Add(Math.Sqrt(energy / count));
        }
        var peak = rms.Max();
        var threshold = Math.Max(Math.Pow(10, -48d / 20), peak * Math.Pow(10, -35d / 20));
        var first = rms.FindIndex(value => value >= threshold);
        var last = rms.FindLastIndex(value => value >= threshold);
        if (first < 0) return [];
        return samples[Math.Max(0, (first - 2) * frame)..Math.Min(samples.Length, (last + 3) * frame)];
    }

    private static object? Scalar(SqliteConnection database, string sql)
    {
        using var command = database.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string NormalizeToken(string value) => new(value
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed unsafe class NativeReferenceRuntime(nint context) : IDisposable
    {
        internal static NativeReferenceRuntime Load(
            string talkerPath, string codecPath, string backend, string? runtimeDirectory)
        {
            QwenNative.GetAbiInfo(out var abi);
            if (abi.AbiVersion != QwenNative.AbiVersion)
                throw new InvalidDataException($"Native ABI {abi.AbiVersion} != managed ABI {QwenNative.AbiVersion}");
            if (runtimeDirectory is not null && QwenNative.BackendLoadFromPath(runtimeDirectory) != 0)
                throw new InvalidOperationException("qt_backend_load_from_path failed: " + LastError());
            var talker = Encoding.UTF8.GetBytes(talkerPath + '\0');
            var codec = Encoding.UTF8.GetBytes(codecPath + '\0');
            var backendBytes = Encoding.UTF8.GetBytes(backend + '\0');
            fixed (byte* talkerPointer = talker)
            fixed (byte* codecPointer = codec)
            fixed (byte* backendPointer = backendBytes)
            {
                QwenNative.InitDefaultParams(out var parameters);
                parameters.TalkerPath = talkerPointer;
                parameters.CodecPath = codecPointer;
                parameters.BackendName = backendPointer;
                parameters.UseFlashAttention = 0;
                var context = QwenNative.Init(ref parameters);
                if (context == 0) throw new InvalidOperationException("qt_init failed: " + LastError());
                return new(context);
            }
        }

        internal NativeReference Extract(float[] samples)
        {
            QwenNative.VoiceRef native = default;
            fixed (float* input = samples)
            {
                var status = QwenNative.ExtractVoiceRef(context, input, samples.Length, out native);
                if (status != 0) throw new InvalidOperationException($"qt_extract_voice_ref failed ({status}): {LastError()}");
            }
            try
            {
                var embedding = new float[native.SpeakerDimension];
                var codes = new int[checked(native.ReferenceLength * native.Codebooks)];
                Marshal.Copy((nint)native.SpeakerEmbedding, embedding, 0, embedding.Length);
                Marshal.Copy((nint)native.Codes, codes, 0, codes.Length);
                return new(embedding, codes, native.ReferenceLength, native.Codebooks);
            }
            finally { QwenNative.VoiceRefFree(ref native); }
        }

        public void Dispose() => QwenNative.Free(context);

        private static string LastError()
        {
            var pointer = QwenNative.LastError();
            return pointer == null ? "unknown native error" : Marshal.PtrToStringUTF8((nint)pointer) ?? "unknown native error";
        }
    }

    private sealed record Options(string SqpackDirectory, string Selection, string Catalog,
        string ModelsManifest, string ModelsDirectory, string[] Qualities, string Backend,
        string? RuntimeDirectory, string? ScenePlan,
        string OutputDatabase, int PackVersion)
    {
        internal static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Arguments must be --name value pairs");
                values.Add(args[index][2..], args[index + 1]);
            }
            string Required(string name) => values.TryGetValue(name, out var value)
                ? Path.GetFullPath(value)
                : throw new ArgumentException($"--{name} is required");
            var output = Required("output");
            var packVersion = Int32.Parse(values.GetValueOrDefault("pack-version") ?? "1");
            return new(
                Required("sqpack"), Required("selection"), Required("catalog"), Required("models-manifest"),
                Required("models-directory"),
                (values.GetValueOrDefault("qualities") ?? "q4,q8").Split(',', StringSplitOptions.RemoveEmptyEntries),
                values.GetValueOrDefault("backend") ?? "CPU",
                values.TryGetValue("runtime-directory", out var runtimeDirectory)
                    ? Path.GetFullPath(runtimeDirectory)
                    : null,
                values.TryGetValue("scene-plan", out var scenePlan) ? Path.GetFullPath(scenePlan) : null,
                output, packVersion);
        }
    }

    private sealed record Source(uint CutsceneId, string Key, string ScdPath, uint SoundNumber,
        string Transcript, double DurationSeconds);
    private sealed record Actor(string ActorToken, IReadOnlyDictionary<string, IReadOnlyList<Source>> Languages);
    private sealed record Selection(int SchemaVersion, string? Eligibility,
        double MinimumSeconds, double MaximumSeconds,
        IReadOnlyList<Actor> Actors, IReadOnlyList<string> Failures);
    private sealed record Catalog(int SchemaVersion, int CatalogVersion, IReadOnlyList<CatalogGroup> Groups);
    private sealed record CatalogGroup(string Id, string Label, IReadOnlyList<string> ActorTokens);
    private sealed record ModelManifest(int SchemaVersion, IReadOnlyList<ModelArtifact> Artifacts);
    private sealed record ModelArtifact(string Id, string FileName, string Sha256);
    private sealed record ScenePlan(int SchemaVersion, IReadOnlyList<SceneNode> Nodes,
        IReadOnlyList<SceneEdge> Edges);
    private sealed record SceneNode(uint CutsceneId, string Language, string NodeId,
        string SheetKeyHash, int Occurrence, uint? ActorNpcBaseId, string? OfficialGroupId,
        string? ActorTokenHash, string NodeKind);
    private sealed record SceneEdge(uint CutsceneId, string Language, string? CurrentNodeId,
        string? NextNodeId);
    private sealed record Quality(string Id, string TalkerPath, string CodecPath, string ModelHash);
    private sealed record NativeReference(float[] Embedding, int[] Codes, int Length, int Codebooks);
}
