using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Numerics;
using System.Globalization;
using Lumina;
using Microsoft.Data.Sqlite;
using Resonance.Game;
using Resonance.Tts;

const int sampleRate = 24_000;
const double minimumSeconds = 0;
const double maximumSeconds = 20;
const int boundarySilenceSamples = 3_600;
const int candidatesPerState = 24;
const int maximumTextsPerReference = 6;

if (args.Length is not 5 and not 10)
    throw new ArgumentException(
        "usage: VoiceReferencePilot <sqpack-directory> <enumeration-json> <actor-token> <language> <output-directory> [<models-manifest> <models-directory> <qualities> <backend> <runtime-directory>]");

var sqpack = Path.GetFullPath(args[0]);
var enumerationPath = Path.GetFullPath(args[1]);
var actorToken = args[2];
var language = args[3];
var output = Path.GetFullPath(args[4]);
var enumeration = JsonSerializer.Deserialize<EnumerationResult>(await File.ReadAllTextAsync(enumerationPath))
                  ?? throw new InvalidDataException("Enumeration is empty");
var game = new GameData(sqpack);
if (actorToken == "*")
{
    if (enumeration.UndubbedActorTokens is null)
        throw new InvalidDataException(
            "Enumeration lacks undubbed actor coverage; regenerate it before batch selection");
    var eligibleActors = enumeration.UndubbedActorTokens.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var shardParts = language.Split('/', StringSplitOptions.TrimEntries);
    var shardIndex = shardParts.Length == 2 ? Int32.Parse(shardParts[0]) : 0;
    var shardCount = shardParts.Length == 2 ? Int32.Parse(shardParts[1]) : 1;
    if (shardIndex < 0 || shardIndex >= shardCount)
        throw new ArgumentOutOfRangeException(nameof(language), "Batch shard must be index/count");
    var actors = new List<SelectedActor>();
    var failures = new List<string>();
    var batchActors = enumeration.Actors.OrderBy(value => value.ActorToken, StringComparer.Ordinal)
        .Where(value => eligibleActors.Contains(value.ActorToken))
        .Where((_, index) => index % shardCount == shardIndex);
    foreach (var currentActor in batchActors)
    {
        var languages = new Dictionary<string, IReadOnlyList<SelectedSource>>(StringComparer.Ordinal);
        foreach (var (currentLanguage, currentSources) in currentActor.Languages.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var result = SelectCandidates(game, currentSources, 1);
            if (result is null)
            {
                failures.Add($"{currentActor.ActorToken}/{currentLanguage}: no clean sub-20-second package");
                continue;
            }
            RankCandidates(result.Candidates);
            var candidate = result.Candidates.MaxBy(value => value.Composite)!;
            languages.Add(currentLanguage, candidate.Lines.Select(line => new SelectedSource(
                line.Source.CutsceneId, line.Source.Key, line.Source.ScdPath, line.SoundNumber,
                line.Source.Transcript, line.Pcm.Length / (double)sampleRate)).ToArray());
            Console.WriteLine($"{currentActor.ActorToken}/{currentLanguage}: ex{result.Expansion} "
                              + $"{candidate.DurationSeconds:F2}s composite={candidate.Composite:F4}");
        }
        if (languages.Count > 0) actors.Add(new(currentActor.ActorToken, languages));
    }
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(
        new BatchSelection(2, "undubbed-occurrence", minimumSeconds, maximumSeconds, actors, failures),
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"shard={shardIndex}/{shardCount} eligible={eligibleActors.Count} actors={actors.Count} "
                      + $"profiles={actors.Sum(value => value.Languages.Count)} "
                      + $"failures={failures.Count} output={output}");
    return;
}
var actor = enumeration.Actors.Single(value =>
    String.Equals(value.ActorToken, actorToken, StringComparison.OrdinalIgnoreCase));
if (!actor.Languages.TryGetValue(language, out var sources))
    throw new InvalidDataException($"{actorToken} has no {language} sources");

var expansionGroups = sources
    .Select(value => (Source: value, Expansion: Expansion(value.ScdPath)))
    .Where(value => value.Expansion >= 0)
    .GroupBy(value => value.Expansion)
    .OrderByDescending(value => value.Key)
    .ToArray();
if (expansionGroups.Length == 0) throw new InvalidDataException("No expansion-scoped sources found");

Directory.CreateDirectory(output);
var pilotResult = SelectCandidates(game, sources, 5);
if (pilotResult is null)
    throw new InvalidDataException("No expansion yielded five clean sub-20-second candidates");
var measured = pilotResult.Measured;
var candidates = pilotResult.Candidates;
var selectedExpansion = pilotResult.Expansion;
RankCandidates(candidates);

var selected = new List<(string Label, string Criterion, Candidate Candidate)>();
AddDistinct("A", "lowest clean pitch variation", candidates.OrderBy(value => value.Analysis.Metrics.PitchRangeSemitones));
AddDistinct("B", "highest pitch range", candidates.OrderByDescending(value => value.Analysis.Metrics.PitchRangeSemitones));
AddDistinct("C", "highest loudness-envelope range", candidates.OrderByDescending(value => value.Analysis.Metrics.RmsRangeDb));
AddDistinct("D", "highest spectral flux", candidates.OrderByDescending(value => value.Analysis.Metrics.SpectralFluxP90));
AddDistinct("E", "highest balanced composite dynamism", candidates.OrderByDescending(value => value.Composite));

var selection = new PilotManifest(
    1, actorToken, language, $"ex{selectedExpansion}", minimumSeconds, maximumSeconds,
    measured.Count, candidates.Count,
    selected.Select(value => new PilotSelection(
        value.Label, value.Criterion, value.Candidate.DurationSeconds, value.Candidate.Analysis.Metrics,
        value.Candidate.Composite,
        value.Candidate.Lines.Select(line => new PilotSource(
            line.Source.CutsceneId, line.Source.Key, line.Source.ScdPath, line.SoundNumber,
            line.Source.Transcript, line.Pcm.Length / (double)sampleRate)).ToArray())).ToArray());

foreach (var item in selected)
{
    var pcm = Chain(item.Candidate.Lines);
    WriteWave(Path.Combine(output, $"{item.Label}-reference.wav"), pcm);
}
await File.WriteAllTextAsync(Path.Combine(output, "pilot-manifest.json"),
    JsonSerializer.Serialize(selection, new JsonSerializerOptions { WriteIndented = true }));
await File.WriteAllTextAsync(Path.Combine(output, "all-candidates.json"),
    JsonSerializer.Serialize(candidates.OrderByDescending(value => value.Composite).Select(value => new
    {
        durationSeconds = value.DurationSeconds,
        value.Analysis.Metrics,
        value.Composite,
        sources = value.Lines.Select(line => line.Source.Key),
    }), new JsonSerializerOptions { WriteIndented = true }));
if (args.Length == 10)
    BuildProfiles(
        selected,
        Path.GetFullPath(args[5]),
        Path.GetFullPath(args[6]),
        args[7].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        args[8], Path.GetFullPath(args[9]),
        output,
        language);
Console.WriteLine($"actor={actorToken} language={language} expansion=ex{selectedExpansion} "
                  + $"lines={measured.Count} candidates={candidates.Count} output={output}");
foreach (var item in selected)
    Console.WriteLine($"{item.Label}: {item.Criterion}; {item.Candidate.DurationSeconds:F2}s; "
                      + $"pitch={item.Candidate.Analysis.Metrics.PitchRangeSemitones:F2}st "
                      + $"rms={item.Candidate.Analysis.Metrics.RmsRangeDb:F2}dB "
                      + $"flux={item.Candidate.Analysis.Metrics.SpectralFluxP90:F4}; "
                      + String.Join(" + ", item.Candidate.Lines.Select(line => line.Source.Key)));
return;

static unsafe void BuildProfiles(
    IReadOnlyList<(string Label, string Criterion, Candidate Candidate)> selected,
    string modelsManifestPath,
    string modelsDirectory,
    IReadOnlyList<string> qualities,
    string backend,
    string runtimeDirectory,
    string output,
    string language)
{
    var manifest = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(modelsManifestPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("Model manifest is empty");
    var databasePath = Path.Combine(output, "pilot-profiles.sqlite3");
    if (File.Exists(databasePath)) File.Delete(databasePath);
    using var database = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString());
    database.Open();
    using (var schema = database.CreateCommand())
    {
        schema.CommandText = """
            CREATE TABLE profile(
              quality TEXT NOT NULL,
              candidate TEXT NOT NULL,
              criterion TEXT NOT NULL,
              transcript TEXT NOT NULL,
              speaker_embedding BLOB NOT NULL,
              rvq_codes BLOB NOT NULL,
              rvq_length INTEGER NOT NULL,
              codebooks INTEGER NOT NULL,
              PRIMARY KEY(quality,candidate));
            """;
        schema.ExecuteNonQuery();
    }
    const string evaluation = "I have faced worse odds than these, and I am still standing.";
    foreach (var quality in qualities)
    {
        var baseModel = manifest.Artifacts.Single(value => value.Id == $"base-{quality}");
        var quantization = quality switch
        {
            "q4" or "1.7b-q4" => "q4",
            "q8" or "1.7b-q8" => "q8",
            _ => throw new InvalidDataException($"Unknown Base quality '{quality}'"),
        };
        var tokenizer = manifest.Artifacts.Single(value => value.Id == $"tokenizer-{quantization}");
        using var runtime = new PilotRuntime(
            Path.Combine(modelsDirectory, baseModel.FileName),
            Path.Combine(modelsDirectory, tokenizer.FileName), backend, runtimeDirectory);
        foreach (var item in selected)
        {
            var pcm = Chain(item.Candidate.Lines);
            var transcript = String.Join(' ', item.Candidate.Lines.Select(value => value.Source.Transcript));
            var reference = runtime.Extract(pcm);
            using (var insert = database.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO profile VALUES($quality,$candidate,$criterion,$transcript,
                      $embedding,$codes,$length,$codebooks)
                    """;
                insert.Parameters.AddWithValue("$quality", quality);
                insert.Parameters.AddWithValue("$candidate", item.Label);
                insert.Parameters.AddWithValue("$criterion", item.Criterion);
                insert.Parameters.AddWithValue("$transcript", transcript);
                insert.Parameters.AddWithValue("$embedding", MemoryMarshal.AsBytes(reference.Embedding.AsSpan()).ToArray());
                insert.Parameters.AddWithValue("$codes", MemoryMarshal.AsBytes(reference.Codes.AsSpan()).ToArray());
                insert.Parameters.AddWithValue("$length", reference.Length);
                insert.Parameters.AddWithValue("$codebooks", reference.Codebooks);
                insert.ExecuteNonQuery();
            }
            var clone = runtime.Synthesize(evaluation, language, transcript, reference);
            WriteWave(Path.Combine(output, $"{item.Label}-{quality}-clone.wav"), clone);
            Console.WriteLine($"{quality}/{item.Label}: embedding={reference.Embedding.Length} "
                              + $"rvq={reference.Length}x{reference.Codebooks} clone={clone.Length / 24000d:F2}s");
        }
    }
    File.WriteAllText(Path.Combine(output, "evaluation-sentence.txt"), evaluation + Environment.NewLine);
}

void AddDistinct(string label, string criterion, IEnumerable<Candidate> ordered)
{
    var value = ordered.FirstOrDefault(candidate => selected.All(existing =>
        !String.Equals(existing.Candidate.Identity, candidate.Identity, StringComparison.Ordinal)));
    if (value is null) throw new InvalidDataException($"Could not select distinct candidate {label}");
    selected.Add((label, criterion, value));
}

static SelectionResult? SelectCandidates(GameData game, IReadOnlyList<Source> sources, int minimumCandidates)
{
    var expansionGroups = sources
        .Select(value => (Source: value, Expansion: Expansion(value.ScdPath)))
        .Where(value => value.Expansion >= 0)
        .GroupBy(value => value.Expansion)
        .OrderByDescending(value => value.Key);
    foreach (var group in expansionGroups.Take(1))
    {
        var measured = new List<MeasuredLine>();
        foreach (var value in group.OrderBy(value => value.Source.CutsceneId)
                                   .ThenBy(value => value.Source.Key, StringComparer.Ordinal))
        {
            try
            {
                var resource = game.GetFile(value.Source.ScdPath);
                if (resource is null) continue;
                var sound = ScdAudioDecoder.ResolveSoleAudioEntry(resource.Data);
                if (sound is null) continue;
                var pcm = TrimSilence(ScdAudioDecoder.Extract(
                    resource.Data, sound.Value, CancellationToken.None));
                if (pcm.Length < sampleRate / 3) continue;
                measured.Add(new(value.Source, sound.Value, pcm, Analyze(pcm)));
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"skip {value.Source.Key}: {error.Message}");
            }
        }
        var candidates = BuildCandidates(measured, group.Key);
        var clean = candidates.Where(IsClean).ToList();
        if (clean.Count == 0)
            clean = candidates.Where(IsUsableFallback).ToList();
        if (clean.Count > 0)
        {
            var bestTier = BestAvailablePunctuationTier(clean);
            var tierCandidates = clean.Where(value => value.PunctuationCoverage == bestTier).ToArray();
            var preferredMask = PreferredMask(bestTier, tierCandidates.Select(value => value.PunctuationMask));
            var maskCandidates = tierCandidates.Where(value => value.PunctuationMask == preferredMask).ToArray();
            var minimumTexts = maskCandidates.Min(value => value.Lines.Length);
            clean = maskCandidates.Where(value => value.Lines.Length == minimumTexts)
                .OrderBy(value => value.PunctuationMask)
                .ThenBy(value => value.Identity, StringComparer.Ordinal)
                .ToList();
        }
        if (clean.Count >= minimumCandidates) return new(group.Key, measured, clean);
    }
    return null;
}

static void RankCandidates(List<Candidate> candidates)
{
    Rank(candidates, value => value.Analysis.Metrics.PitchRangeSemitones,
        (candidate, rank) => candidate.PitchRank = rank);
    Rank(candidates, value => value.Analysis.Metrics.RmsRangeDb,
        (candidate, rank) => candidate.RmsRank = rank);
    Rank(candidates, value => value.Analysis.Metrics.SpectralFluxP90,
        (candidate, rank) => candidate.FluxRank = rank);
    foreach (var candidate in candidates)
        candidate.Composite = Mean(candidate.PitchRank, candidate.RmsRank, candidate.FluxRank);
}

static List<Candidate> BuildCandidates(IReadOnlyList<MeasuredLine> lines, int expansion)
{
    var maximumSamples = (int)Math.Floor(maximumSeconds * sampleRate);
    var textMasks = lines.Select(line => PunctuationMask(
        line.Source.Transcript, line.Source.ScdPath.EndsWith("_ja.scd", StringComparison.Ordinal))).ToArray();
    var states = new Dictionary<(int Mask, int TextCount), List<SearchPackage>>
    {
        [(0, 0)] = [new([], 0, 0)],
    };
    for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
    {
        var line = lines[lineIndex];
        var snapshot = states.Values.SelectMany(value => value).ToArray();
        foreach (var package in snapshot)
        {
            if (package.LineIndices.Length >= maximumTextsPerReference) continue;
            var samples = package.SampleCount + line.Pcm.Length
                          + (package.LineIndices.Length == 0 ? 0 : boundarySilenceSamples);
            if (samples >= maximumSamples) continue;
            var indices = new int[package.LineIndices.Length + 1];
            package.LineIndices.CopyTo(indices, 0);
            indices[^1] = lineIndex;
            var mask = package.PunctuationMask | textMasks[lineIndex];
            var candidate = new SearchPackage(indices, samples, mask);
            var key = (mask, indices.Length);
            if (!states.TryGetValue(key, out var bucket)) states[key] = bucket = [];
            AddSearchCandidate(bucket, candidate, lines);
        }
    }
    return states.Values.SelectMany(value => value)
        .Where(value => value.SampleCount > 0 && value.LineIndices.Length > 0)
        .Select(value =>
        {
            var selected = value.LineIndices.Select(index => lines[index]).ToArray();
            return new Candidate(expansion, selected, value.SampleCount / (double)sampleRate,
                Combine(selected), value.PunctuationMask);
        })
        .DistinctBy(value => value.Identity, StringComparer.Ordinal)
        .ToList();
}

static int BestAvailablePunctuationTier(IReadOnlyCollection<Candidate> candidates)
{
    // Exact masks preserve the complete lattice while searching: six pairs,
    // four triples, one full set. Only after every mask at a tier has survived
    // duration/cleanliness filtering do we select the highest non-empty tier.
    for (var tier = 4; tier >= 2; tier--)
        if (candidates.Any(value => value.PunctuationCoverage == tier)) return tier;
    return candidates.Count == 0 ? 0 : candidates.Max(value => value.PunctuationCoverage);
}

static int PreferredMask(int tier, IEnumerable<int> masks)
{
    var available = masks.Distinct().Order().ToArray();
    var preferred = tier switch
    {
        3 => 1 | 4 | 8,
        2 => 1 | 4,
        _ => -1,
    };
    return available.Contains(preferred) ? preferred : available[0];
}

static void AddSearchCandidate(
    List<SearchPackage> bucket,
    SearchPackage candidate,
    IReadOnlyList<MeasuredLine> lines)
{
    if (bucket.Any(value => value.Identity == candidate.Identity)) return;
    bucket.Add(candidate);
    if (bucket.Count <= candidatesPerState) return;
    var protectedCandidates = new HashSet<string>(StringComparer.Ordinal);
    Protect(value => Proxy(value, metric => metric.PitchRangeSemitones));
    Protect(value => Proxy(value, metric => metric.RmsRangeDb));
    Protect(value => Proxy(value, metric => metric.SpectralFluxP90));
    protectedCandidates.Add(bucket.MinBy(value => value.SampleCount)!.Identity);
    var remove = bucket.Where(value => !protectedCandidates.Contains(value.Identity))
        .MinBy(value => SearchDynamism(value, lines)) ?? bucket.MinBy(value => SearchDynamism(value, lines))!;
    bucket.Remove(remove);

    void Protect(Func<SearchPackage, double> metric)
    {
        foreach (var value in bucket.OrderByDescending(metric).Take(2))
            protectedCandidates.Add(value.Identity);
    }

    double Proxy(SearchPackage value, Func<AudioMetrics, double> metric) =>
        value.LineIndices.Sum(index => metric(lines[index].Analysis.Metrics));
}

static double SearchDynamism(SearchPackage value, IReadOnlyList<MeasuredLine> lines)
{
    return value.LineIndices.Sum(index =>
    {
        var metrics = lines[index].Analysis.Metrics;
        return metrics.PitchRangeSemitones / 12d
               + metrics.RmsRangeDb / 20d
               + metrics.SpectralFluxP90 * 5d;
    });
}

static int PunctuationMask(string text, bool japanese)
{
    const int period = 1;
    const int ellipsis = 2;
    const int question = 4;
    const int exclamation = 8;
    var result = 0;
    var clauseStart = 0;
    for (var index = 0; index < text.Length; index++)
    {
        var character = text[index];
        var terminator = 0;
        var end = index + 1;
        if (character == '.')
        {
            while (end < text.Length && text[end] == '.') end++;
            terminator = end - index >= 2 ? ellipsis : period;
        }
        else if (character is '…' or '⋯')
        {
            while (end < text.Length && text[end] == character) end++;
            terminator = ellipsis;
        }
        else if (character == '・' && end < text.Length && text[end] == '・')
        {
            while (end < text.Length && text[end] == '・') end++;
            terminator = ellipsis;
        }
        else if (character is '?' or '？') terminator = question;
        else if (character is '!' or '！') terminator = exclamation;
        else if (japanese && character == '。')
            terminator = IsJapaneseInterrogative(text.AsSpan(clauseStart, index - clauseStart))
                ? question
                : period;
        if (terminator == 0) continue;
        var clause = text.AsSpan(clauseStart, index - clauseStart);
        if (japanese ? JapaneseSubstance(clause) >= 1 : WordCount(clause) >= 1)
            result |= terminator;
        clauseStart = end;
        index = end - 1;
    }
    return result;
}

static bool IsJapaneseInterrogative(ReadOnlySpan<char> clause)
{
    var value = clause.TrimEnd().ToString();
    return value.EndsWith('か') || value.EndsWith('の')
        || value.EndsWith("だろう", StringComparison.Ordinal)
        || value.EndsWith("でしょう", StringComparison.Ordinal);
}

static int JapaneseSubstance(ReadOnlySpan<char> value)
{
    var count = 0;
    foreach (var rune in value.EnumerateRunes())
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber)
            count++;
    }
    return count;
}

static int WordCount(ReadOnlySpan<char> value)
{
    var count = 0;
    var inside = false;
    foreach (var character in value)
    {
        if (Char.IsWhiteSpace(character)) inside = false;
        else if (!inside) { count++; inside = true; }
    }
    return count;
}

static bool IsClean(Candidate value) =>
    value.Analysis.Metrics.VoicedRatio >= 0.2
    && value.Analysis.Metrics.ClippingRatio <= 0.001
    && value.Analysis.Metrics.Peak >= 0.015;

static bool IsUsableFallback(Candidate value) =>
    value.Analysis.Metrics.VoicedRatio >= 0.05
    && value.Analysis.Metrics.ClippingRatio <= 0.01
    && value.Analysis.Metrics.Peak >= 0.005;

static float[] Chain(IReadOnlyList<MeasuredLine> lines)
{
    var length = lines.Sum(value => value.Pcm.Length) + Math.Max(0, lines.Count - 1) * boundarySilenceSamples;
    var result = new float[length];
    var offset = 0;
    for (var index = 0; index < lines.Count; index++)
    {
        if (index > 0) offset += boundarySilenceSamples;
        lines[index].Pcm.CopyTo(result, offset);
        offset += lines[index].Pcm.Length;
    }
    return result;
}

static float[] TrimSilence(float[] samples)
{
    const int frame = 480;
    if (samples.Length <= frame) return samples;
    var rms = new List<double>();
    for (var offset = 0; offset < samples.Length; offset += frame)
    {
        var count = Math.Min(frame, samples.Length - offset);
        rms.Add(Math.Sqrt(samples.AsSpan(offset, count).ToArray().Average(value => value * value)));
    }
    var peak = rms.Max();
    var threshold = Math.Max(DbToLinear(-48), peak * DbToLinear(-35));
    var first = rms.FindIndex(value => value >= threshold);
    var last = rms.FindLastIndex(value => value >= threshold);
    if (first < 0) return [];
    var start = Math.Max(0, (first - 2) * frame);
    var end = Math.Min(samples.Length, (last + 3) * frame);
    return samples[start..end];
}

static Analysis Analyze(float[] samples)
{
    const int frameSize = 960;
    const int hop = 480;
    var rmsDb = new List<double>();
    var pitch = new List<double>();
    var flux = new List<double>();
    double[]? previousSpectrum = null;
    var peak = 0d;
    var clipped = 0;
    var pitchFrame = new float[frameSize / 3];
    for (var index = 0; index < samples.Length; index++)
    {
        var absolute = Math.Abs(samples[index]);
        peak = Math.Max(peak, absolute);
        if (absolute >= 0.999) clipped++;
    }
    for (var offset = 0; offset + frameSize <= samples.Length; offset += hop)
    {
        var frame = samples.AsSpan(offset, frameSize);
        var rms = Math.Sqrt(frame.ToArray().Average(value => value * value));
        var db = LinearToDb(rms);
        rmsDb.Add(db);
        if (db > -45)
        {
            for (var index = 0; index < pitchFrame.Length; index++)
                pitchFrame[index] = (frame[index * 3] + frame[index * 3 + 1] + frame[index * 3 + 2]) / 3;
            var f0 = EstimatePitch(pitchFrame, sampleRate / 3);
            if (f0 is >= 55 and <= 350) pitch.Add(f0);
        }
        var spectrum = Spectrum(frame, 128);
        if (previousSpectrum is not null)
        {
            var positive = 0d;
            var total = 1e-9;
            for (var bin = 0; bin < spectrum.Length; bin++)
            {
                positive += Math.Max(0, spectrum[bin] - previousSpectrum[bin]);
                total += previousSpectrum[bin];
            }
            flux.Add(positive / total);
        }
        previousSpectrum = spectrum;
    }
    var activeRms = rmsDb.Where(value => value > -45).ToArray();
    var pitchSemitones = pitch.Count == 0
        ? []
        : pitch.Select(value => 12 * Math.Log2(value / Median(pitch))).ToArray();
    var metrics = Metrics(
        activeRms.Length / (double)Math.Max(1, rmsDb.Count),
        pitch.ToArray(), activeRms, flux.ToArray(), peak,
        clipped / (double)Math.Max(1, samples.Length));
    return new(metrics, pitch.ToArray(), activeRms, flux.ToArray(), samples.Length, clipped);
}

static Analysis Combine(IReadOnlyList<MeasuredLine> lines)
{
    var pitch = lines.SelectMany(value => value.Analysis.PitchHz).ToArray();
    var rms = lines.SelectMany(value => value.Analysis.ActiveRmsDb).ToArray();
    var flux = lines.SelectMany(value => value.Analysis.SpectralFlux).ToArray();
    var samples = lines.Sum(value => value.Analysis.SampleCount);
    var clipped = lines.Sum(value => value.Analysis.ClippedSamples);
    var totalFrames = lines.Sum(value => value.Analysis.ActiveRmsDb.Length /
        Math.Max(value.Analysis.Metrics.VoicedRatio, 1e-9));
    var voicedRatio = rms.Length / Math.Max(1, totalFrames);
    return new(
        Metrics(voicedRatio, pitch, rms, flux, lines.Max(value => value.Analysis.Metrics.Peak),
            clipped / (double)Math.Max(1, samples)),
        pitch, rms, flux, samples, clipped);
}

static AudioMetrics Metrics(double voicedRatio, double[] pitch, double[] activeRms,
    double[] flux, double peak, double clippingRatio)
{
    var normalizedPitch = NormalizePitchOctaves(pitch);
    var pitchSemitones = normalizedPitch.Length == 0
        ? []
        : normalizedPitch.Select(value => 12 * Math.Log2(value / Median(normalizedPitch))).ToArray();
    return new(
        voicedRatio,
        pitch.Length,
        normalizedPitch.Length == 0 ? 0 : Percentile(normalizedPitch, 0.9) - Percentile(normalizedPitch, 0.1),
        pitchSemitones.Length == 0 ? 0 : Percentile(pitchSemitones, 0.9) - Percentile(pitchSemitones, 0.1),
        StandardDeviation(pitchSemitones),
        activeRms.Length == 0 ? 0 : Percentile(activeRms, 0.9) - Percentile(activeRms, 0.1),
        StandardDeviation(activeRms),
        flux.Length == 0 ? 0 : Percentile(flux, 0.9),
        peak,
        clippingRatio);
}

static double[] NormalizePitchOctaves(double[] pitch)
{
    if (pitch.Length == 0) return [];
    var center = Median(pitch);
    return pitch.Select(value => value * Math.Pow(2, -Math.Round(Math.Log2(value / center)))).ToArray();
}

static double EstimatePitch(ReadOnlySpan<float> frame, int rate)
{
    var minLag = rate / 350;
    var maxLag = rate / 55;
    var difference = new double[maxLag + 1];
    for (var lag = 1; lag <= maxLag; lag++)
    {
        var sum = 0d;
        for (var index = 0; index < frame.Length - lag; index++)
        {
            var delta = frame[index] - frame[index + lag];
            sum += delta * delta;
        }
        difference[lag] = sum;
    }
    var cumulative = 0d;
    var normalized = new double[maxLag + 1];
    normalized[0] = 1;
    for (var lag = 1; lag <= maxLag; lag++)
    {
        cumulative += difference[lag];
        normalized[lag] = cumulative <= 1e-12 ? 1 : difference[lag] * lag / cumulative;
    }
    var selected = 0;
    for (var lag = minLag; lag < maxLag; lag++)
    {
        if (normalized[lag] >= 0.15) continue;
        while (lag + 1 <= maxLag && normalized[lag + 1] < normalized[lag]) lag++;
        selected = lag;
        break;
    }
    if (selected == 0)
    {
        selected = Enumerable.Range(minLag, maxLag - minLag + 1)
            .MinBy(lag => normalized[lag]);
        if (normalized[selected] > 0.35) return 0;
    }
    var refined = (double)selected;
    if (selected > minLag && selected < maxLag)
    {
        var left = normalized[selected - 1];
        var center = normalized[selected];
        var right = normalized[selected + 1];
        var denominator = left - 2 * center + right;
        if (Math.Abs(denominator) > 1e-12)
            refined += 0.5 * (left - right) / denominator;
    }
    return rate / refined;
}

static double[] Spectrum(ReadOnlySpan<float> frame, int bins)
{
    var size = 1;
    while (size < frame.Length) size <<= 1;
    var values = new Complex[size];
    for (var index = 0; index < frame.Length; index++)
    {
        var window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (frame.Length - 1));
        values[index] = new Complex(frame[index] * window, 0);
    }
    for (var index = 1; index < size; index++)
    {
        var reversed = BitReverse(index, BitOperations.Log2((uint)size));
        if (index < reversed) (values[index], values[reversed]) = (values[reversed], values[index]);
    }
    for (var length = 2; length <= size; length <<= 1)
    {
        var root = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
        for (var offset = 0; offset < size; offset += length)
        {
            var factor = Complex.One;
            for (var index = 0; index < length / 2; index++)
            {
                var even = values[offset + index];
                var odd = values[offset + index + length / 2] * factor;
                values[offset + index] = even + odd;
                values[offset + index + length / 2] = even - odd;
                factor *= root;
            }
        }
    }
    return values.Take(Math.Min(bins, size / 2)).Select(value => value.Magnitude).ToArray();
}

static int BitReverse(int value, int bits)
{
    var result = 0;
    for (var index = 0; index < bits; index++)
    {
        result = (result << 1) | (value & 1);
        value >>= 1;
    }
    return result;
}

static void Rank(List<Candidate> values, Func<Candidate, double> metric, Action<Candidate, double> assign)
{
    var ordered = values.OrderBy(metric).ToArray();
    for (var index = 0; index < ordered.Length; index++)
        assign(ordered[index], ordered.Length == 1 ? 1 : index / (double)(ordered.Length - 1));
}

static double Mean(params double[] values) => values.Average();
static double StandardDeviation(IReadOnlyCollection<double> values)
{
    if (values.Count == 0) return 0;
    var mean = values.Average();
    return Math.Sqrt(values.Average(value => (value - mean) * (value - mean)));
}
static double Median(IReadOnlyCollection<double> values) => Percentile(values, 0.5);
static double Percentile(IReadOnlyCollection<double> values, double percentile)
{
    if (values.Count == 0) return 0;
    var sorted = values.Order().ToArray();
    var position = percentile * (sorted.Length - 1);
    var lower = (int)Math.Floor(position);
    var upper = (int)Math.Ceiling(position);
    return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
}
static double LinearToDb(double value) => 20 * Math.Log10(Math.Max(value, 1e-9));
static double DbToLinear(double value) => Math.Pow(10, value / 20);
static int Expansion(string path)
{
    var parts = path.Split('/');
    if (parts.Length < 2 || !parts[1].StartsWith("ex", StringComparison.Ordinal)
        || !Int32.TryParse(parts[1].AsSpan(2), out var expansion)) return -1;
    return expansion;
}
static void WriteWave(string path, float[] samples)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    var dataSize = checked(samples.Length * sizeof(short));
    writer.Write("RIFF"u8); writer.Write(36 + dataSize); writer.Write("WAVE"u8);
    writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
    writer.Write(sampleRate); writer.Write(sampleRate * sizeof(short)); writer.Write((short)2); writer.Write((short)16);
    writer.Write("data"u8); writer.Write(dataSize);
    foreach (var sample in samples)
        writer.Write((short)Math.Clamp(Math.Round(sample * short.MaxValue), short.MinValue, short.MaxValue));
}

sealed record Source(uint CutsceneId, string Key, string ScdPath, string Transcript);
sealed record Actor(string ActorToken, IReadOnlyDictionary<string, IReadOnlyList<Source>> Languages);
sealed record EnumerationResult(int SchemaVersion, int ParsedCutscenes, int MissingCutb,
    IReadOnlyList<string>? UndubbedActorTokens, IReadOnlyList<Actor> Actors);
sealed record SelectionResult(int Expansion, List<MeasuredLine> Measured, List<Candidate> Candidates);
sealed record SelectedSource(uint CutsceneId, string Key, string ScdPath, uint SoundNumber,
    string Transcript, double DurationSeconds);
sealed record SelectedActor(string ActorToken,
    IReadOnlyDictionary<string, IReadOnlyList<SelectedSource>> Languages);
sealed record BatchSelection(int SchemaVersion, string Eligibility,
    double MinimumSeconds, double MaximumSeconds,
    IReadOnlyList<SelectedActor> Actors, IReadOnlyList<string> Failures);
sealed record Analysis(AudioMetrics Metrics, double[] PitchHz, double[] ActiveRmsDb,
    double[] SpectralFlux, int SampleCount, int ClippedSamples);
sealed record MeasuredLine(Source Source, uint SoundNumber, float[] Pcm, Analysis Analysis);
sealed record SearchPackage(
    int[] LineIndices,
    int SampleCount,
    int PunctuationMask)
{
    public string Identity { get; } = String.Join(',', LineIndices);
}
sealed class Candidate(
    int expansion,
    MeasuredLine[] lines,
    double durationSeconds,
    Analysis analysis,
    int punctuationMask)
{
    public int Expansion { get; } = expansion;
    public MeasuredLine[] Lines { get; } = lines;
    public double DurationSeconds { get; } = durationSeconds;
    public Analysis Analysis { get; } = analysis;
    public int PunctuationMask { get; } = punctuationMask;
    public int PunctuationCoverage { get; } = BitOperations.PopCount((uint)punctuationMask);
    public string Identity { get; } = String.Join('|', lines.Select(value => value.Source.Key));
    public double PitchRank { get; set; }
    public double RmsRank { get; set; }
    public double FluxRank { get; set; }
    public double Composite { get; set; }
}
sealed record AudioMetrics(double VoicedRatio, int PitchFrameCount, double PitchRangeHz,
    double PitchRangeSemitones, double PitchStandardDeviationSemitones, double RmsRangeDb,
    double RmsStandardDeviationDb, double SpectralFluxP90, double Peak, double ClippingRatio);
sealed record PilotSource(uint CutsceneId, string Key, string ScdPath, uint SoundNumber,
    string Transcript, double DurationSeconds);
sealed record PilotSelection(string Label, string Criterion, double DurationSeconds,
    AudioMetrics Metrics, double Composite, IReadOnlyList<PilotSource> Sources);
sealed record PilotManifest(int SchemaVersion, string ActorToken, string Language, string Expansion,
    double MinimumSeconds, double MaximumSeconds, int MeasuredLines, int ViableCandidates,
    IReadOnlyList<PilotSelection> Selections);
sealed record ModelManifest(int SchemaVersion, IReadOnlyList<ModelArtifact> Artifacts);
sealed record ModelArtifact(string Id, string FileName, string Sha256);
sealed record NativeReference(float[] Embedding, int[] Codes, int Length, int Codebooks);

sealed unsafe class PilotRuntime : IDisposable
{
    private readonly nint context;

    public PilotRuntime(string talkerPath, string codecPath, string backend, string runtimeDirectory)
    {
        if (!File.Exists(talkerPath)) throw new FileNotFoundException("Base model is missing", talkerPath);
        if (!File.Exists(codecPath)) throw new FileNotFoundException("Tokenizer is missing", codecPath);
        QwenNative.GetAbiInfo(out var abi);
        if (abi.AbiVersion != QwenNative.AbiVersion)
            throw new InvalidDataException($"Native ABI {abi.AbiVersion} != managed ABI {QwenNative.AbiVersion}");
        if (QwenNative.BackendLoadFromPath(runtimeDirectory) != 0)
            throw new InvalidOperationException("qt_backend_load_from_path failed: " + LastError());
        QwenNative.InitDefaultParams(out var parameters);
        using var talker = new Utf8(talkerPath);
        using var codec = new Utf8(codecPath);
        using var backendName = new Utf8(backend);
        parameters.TalkerPath = talker.Pointer;
        parameters.CodecPath = codec.Pointer;
        parameters.BackendName = backendName.Pointer;
        parameters.MaxBatch = 1;
        context = QwenNative.Init(ref parameters);
        if (context == 0) throw new InvalidOperationException("qt_init failed: " + LastError());
    }

    public NativeReference Extract(float[] samples)
    {
        QwenNative.VoiceRef native = default;
        fixed (float* input = samples)
        {
            var status = QwenNative.ExtractVoiceRef(context, input, samples.Length, out native);
            if (status != 0)
                throw new InvalidOperationException($"qt_extract_voice_ref failed ({status}): {LastError()}");
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

    public float[] Synthesize(string textValue, string languageValue, string transcript, NativeReference reference)
    {
        QwenNative.TtsDefaultParams(out var parameters);
        using var text = new Utf8(textValue);
        using var language = new Utf8(languageValue);
        using var referenceText = new Utf8(transcript);
        parameters.Text = text.Pointer;
        parameters.Language = language.Pointer;
        parameters.ReferenceText = referenceText.Pointer;
        parameters.Seed = 123456789;
        parameters.MaxNewTokens = 2048;
        QwenNative.Audio audio = default;
        fixed (float* embedding = reference.Embedding)
        fixed (int* codes = reference.Codes)
        {
            parameters.ReferenceSpeakerEmbedding = embedding;
            parameters.ReferenceSpeakerDimension = reference.Embedding.Length;
            parameters.ReferenceCodes = codes;
            parameters.ReferenceLength = reference.Length;
            var status = QwenNative.Synthesize(context, ref parameters, out audio);
            if (status != 0) throw new InvalidOperationException($"qt_synthesize failed ({status}): {LastError()}");
        }
        try
        {
            if (audio.SampleRate != 24_000 || audio.Channels != 1 || audio.SampleCount <= 0)
                throw new InvalidDataException("Native clone output shape is incompatible");
            var samples = new float[audio.SampleCount];
            Marshal.Copy((nint)audio.Samples, samples, 0, samples.Length);
            return samples;
        }
        finally { QwenNative.AudioFree(ref audio); }
    }

    public void Dispose() => QwenNative.Free(context);

    private static string LastError()
    {
        var pointer = QwenNative.LastError();
        return pointer == null ? "unknown native error" : Marshal.PtrToStringUTF8((nint)pointer) ?? "unknown native error";
    }

    private sealed class Utf8 : IDisposable
    {
        private readonly nint memory;
        public byte* Pointer => (byte*)memory;
        public Utf8(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + '\0');
            memory = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, memory, bytes.Length);
        }
        public void Dispose() => Marshal.FreeHGlobal(memory);
    }
}
