using Resonance.Audio;
using Resonance.Game;
using Resonance.Scheduling;

namespace Resonance.Tests;

public sealed class GameMixerAudioBackendTests
{
    [Theory]
    [InlineData("cut/ex5/sound/voice/example.scd", "cut/ex5/sound/voice/example.scd")]
    [InlineData("cut/ffxiv/sound/voice/example.scd", "cut/ffxiv/sound/voice/example.scd")]
    [InlineData("sound/voice/example.scd", "sound/voice/example.scd")]
    [InlineData("\\cut\\ex4\\sound\\voice\\example.scd", "cut/ex4/sound/voice/example.scd")]
    public void ExtractedScdPathsNormalizeToLuminaResourcePaths(string stored, string expected)
        => Assert.Equal(expected, NativeScdTemplateLoader.NormalizeGamePath(stored));

    [Fact]
    public void GeneratedPlaybackPathPreservesVerifiedTemplateResourceDirectory()
        => Assert.Equal(
            "cut/ex5/sound/voicem/resonance-abc-2a.scd",
            FfxivGameMixerAudioBackend.CreateTemplateSiblingPath(
                "cut/ex5/sound/voicem/manfst0000.scd", "abc", 42));

    [Fact]
    public void ScdHasOneBoundedMonoVoiceAudioEntryAndContentAddressedPath()
    {
        var source = Enumerable.Repeat(0.1f, 1_000).ToArray();
        var prepared = GameMixerPcmEncoder.PrepareMono44100(source, applyBaseCloneCorrection: false);
        var asset = ScdFileBuilder.Build(prepared.Samples, prepared.SampleRate);

        Assert.True(ScdFileBuilder.TryReadLayout(asset.Bytes.Span, out var layout, out var error), error);
        Assert.Equal(1, layout.SoundCount);
        Assert.Equal(1, layout.TrackCount);
        Assert.Equal(1, layout.AudioCount);
        Assert.Equal(1, layout.Channels);
        Assert.Equal(44_100, layout.SampleRate);
        Assert.Equal(ScdFileBuilder.MsAdpcmFormat, layout.AudioFormat);
        Assert.Equal($"sound/resonance/{asset.ContentHash}.scd", asset.VirtualPath);
        Assert.Equal(asset.ContentHash, Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(asset.Bytes.Span)).ToLowerInvariant());
        Assert.InRange(asset.Bytes.Length, 1, ScdFileBuilder.MaxScdBytes);
    }

    [Fact]
    public void GeneratedScdRoundTripsThroughIndependentGameScdDecoder()
    {
        var source = Enumerable.Range(0, 24_000)
            .Select(index => 0.2f * MathF.Sin(2 * MathF.PI * 220 * index / 24_000f))
            .ToArray();
        var prepared = GameMixerPcmEncoder.PrepareMono44100(source, applyBaseCloneCorrection: false);
        var asset = ScdFileBuilder.Build(prepared.Samples, prepared.SampleRate);

        var decoded = ScdAudioDecoder.Extract(
            asset.Bytes.ToArray(), 0, TestContext.Current.CancellationToken);

        Assert.InRange(decoded.Length, 23_000, 25_000);
        Assert.True(decoded.All(float.IsFinite));
        Assert.InRange(decoded.Max(value => Math.Abs(value)), 0.05f, 0.5f);
    }

    [Fact]
    public void NativeTemplateSplicePreservesRoutingAndRoundTripsAudio()
    {
        var source = Enumerable.Range(0, 24_000)
            .Select(index => 0.1f * MathF.Sin(2 * MathF.PI * 220 * index / 24_000f))
            .ToArray();
        var prepared = GameMixerPcmEncoder.PrepareMono44100(source, false);
        var template = new byte[0x300];
        "SEDBSSCF"u8.CopyTo(template);
        BitConverter.GetBytes((short)1).CopyTo(template, 0x30);
        BitConverter.GetBytes((short)1).CopyTo(template, 0x32);
        BitConverter.GetBytes((short)1).CopyTo(template, 0x34);
        BitConverter.GetBytes(0x120).CopyTo(template, 0x50);
        BitConverter.GetBytes(0x150).CopyTo(template, 0x60);
        BitConverter.GetBytes(0x230).CopyTo(template, 0x70);
        BitConverter.GetBytes(0x0a0).CopyTo(template, 0x80);
        template.AsSpan(0xa0, 0x190).Fill(0x5a);
        var generated = ScdFileBuilder.Build(prepared.Samples);
        generated.Bytes.Span.Slice(0x140, 82).CopyTo(template.AsSpan(0x230));
        BitConverter.GetBytes(44_102).CopyTo(template, 0x238);
        BitConverter.GetBytes(44_102).CopyTo(template, 0x254);
        BitConverter.GetBytes((short)22).CopyTo(template, 0x25c);
        BitConverter.GetBytes((short)32).CopyTo(template, 0x262);
        BitConverter.GetBytes((int)Math.Ceiling(44_102d * 22 / 32)).CopyTo(template, 0x258);
        BitConverter.GetBytes(0x01000000).CopyTo(template, 0x24c);

        var asset = ScdFileBuilder.BuildFromNativeTemplate(prepared.Samples, template);

        Assert.Equal(template.AsSpan(0xa0, 0x80).ToArray(), asset.Bytes.Span[0xa0..0x120].ToArray());
        Assert.Equal(44_102, BitConverter.ToInt32(asset.Bytes.Span[0x238..0x23c]));
        Assert.Equal((short)22, BitConverter.ToInt16(asset.Bytes.Span[0x25c..0x25e]));
        Assert.Equal(0, asset.Bytes.Length & 15);
        Assert.Equal(asset.Bytes.Length, BitConverter.ToInt32(asset.Bytes.Span[0x10..0x14]));
        var decoded = ScdAudioDecoder.Extract(asset.Bytes.ToArray(), 0, TestContext.Current.CancellationToken);
        Assert.InRange(decoded.Length, 23_000, 25_000);
    }

    [Fact]
    public void ScdLayoutRejectsMalformedOrAmbiguousHeaders()
    {
        Assert.False(ScdFileBuilder.TryReadLayout(new byte[0x54], out _, out var shortError));
        Assert.False(String.IsNullOrWhiteSpace(shortError));

        var malformed = new byte[0x80];
        "SEDBSSCF"u8.CopyTo(malformed);
        BitConverter.GetBytes((short)2).CopyTo(malformed, 0x34);
        Assert.False(ScdFileBuilder.TryReadLayout(malformed, out _, out var countError));
        Assert.False(String.IsNullOrWhiteSpace(countError));
    }

    [Fact]
    public void CorrectionMarkerAndResamplingAreAppliedBeforeScdEncoding()
    {
        var source = new[] { 0.25f, -0.5f, 0.75f, -1f };
        var withoutCorrection = GameMixerPcmEncoder.PrepareMono44100(source, false);
        var withCorrection = GameMixerPcmEncoder.PrepareMono44100(source, true);

        Assert.False(withoutCorrection.BaseCloneCorrectionApplied);
        Assert.True(withCorrection.BaseCloneCorrectionApplied);
        Assert.Equal(44_100, withoutCorrection.SampleRate);
        Assert.False(withoutCorrection.Samples.SequenceEqual(withCorrection.Samples));
    }

    [Theory]
    [InlineData(-18, 4)]
    [InlineData(-8, -6)]
    public void BaseCloneNormalizationTargetsMeasuredNativeSpeechLevelWithinBoundedGain(
        double inputDbfs, double expectedGainDb)
    {
        var amplitude = (float)Math.Pow(10, inputDbfs / 20);
        var source = Enumerable.Repeat(amplitude, 24_000).ToArray();

        var gain = GameMixerPcmEncoder.NormalizeBaseCloneInPlace(source);

        Assert.InRange(gain, expectedGainDb - 0.001, expectedGainDb + 0.001);
        var rmsDb = 20 * Math.Log10(Math.Sqrt(source.Average(value => value * value)));
        Assert.InRange(rmsDb, -14.001, -13.999);
    }

    [Fact]
    public void BaseCloneNormalizationLimitsBoostAndProtectsPeak()
    {
        var quiet = Enumerable.Repeat((float)Math.Pow(10, -30 / 20d), 24_000).ToArray();
        Assert.Equal(6, GameMixerPcmEncoder.NormalizeBaseCloneInPlace(quiet), 6);

        var transient = Enumerable.Repeat(0.1f, 24_000).ToArray();
        transient[12_000] = 1f;
        var gain = GameMixerPcmEncoder.NormalizeBaseCloneInPlace(transient);

        Assert.InRange(gain, -0.501, -0.499);
        Assert.InRange(transient.Max(), 0.944f, 0.945f);
    }

    [Fact]
    public void SceneSelectionAlwaysLocksNativeOutputAndNeverFallsBack()
    {
        var selection = new AudioBackendSessionLock();

        Assert.Equal(AudioOutputBackend.FfxivGameMixer, selection.SelectForScene());
        Assert.True(selection.IsSceneLocked);
        Assert.Equal(AudioOutputBackend.FfxivGameMixer, selection.SelectForDebug());

        Assert.Equal(AudioOutputBackend.FfxivGameMixer, selection.EndScene());
        Assert.Equal(AudioOutputBackend.FfxivGameMixer, selection.SelectForScene());
        Assert.Equal(AudioOutputBackend.FfxivGameMixer, selection.SelectForDebug());
    }

    [Fact]
    public void AssetStoreRetainsThroughGraceThenCleansBoundedAsset()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-game-mixer-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new GameMixerAssetStore(root, TimeSpan.FromSeconds(1));
            var asset = store.Publish(new byte[] { 1, 2, 3 });
            Assert.True(File.Exists(asset.LocalPath));
            Assert.True(store.Retain(asset.ContentHash));
            Assert.True(store.Release(asset.ContentHash, DateTimeOffset.UtcNow));
            Assert.Equal(0, store.Cleanup(DateTimeOffset.UtcNow));
            Assert.True(store.Cleanup(DateTimeOffset.UtcNow.AddSeconds(2)) > 0);
            Assert.False(File.Exists(asset.LocalPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AssetStoreCanOverrideAnExistingSqPackGamePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-game-mixer-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new GameMixerAssetStore(root);
            const string gamePath = "cut/ex5/sound/voicem/example.scd";
            var asset = store.Publish(new byte[] { 1, 2, 3 }, gamePath);
            Assert.Equal(gamePath, asset.VirtualPath);
            Assert.True(File.Exists(asset.LocalPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BackendDoesNotPublishOrPlayBeforeProducerCompletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-game-mixer-" + Guid.NewGuid().ToString("N"));
        var bridge = new TestResourceOverride();
        var player = new TestSoundPlayer();
        try
        {
            using var backend = new FfxivGameMixerAudioBackend(
                root, bridge, player, TimeSpan.Zero, TimeSpan.Zero);
            using var line = CreateLine();
            line.Audio.TryWrite(new[] { 0.1f, 0.2f });
            backend.Play(line, 1f, _ => { }, _ => { }, (_, error) => throw error);
            await Task.Delay(25, TestContext.Current.CancellationToken);
            Assert.Equal(0, player.PlayCount);

            line.Audio.Complete();
            await player.Played.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(1, player.PlayCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AcceptedNativeDispatchFinishesByEncodedDurationWithoutObservation()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-game-mixer-" + Guid.NewGuid().ToString("N"));
        var bridge = new TestResourceOverride();
        var player = new TestSoundPlayer();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var backend = new FfxivGameMixerAudioBackend(
                root, bridge, player, TimeSpan.Zero, TimeSpan.Zero);
            using var line = CreateLine();
            line.Audio.TryWrite(Enumerable.Repeat(0.1f, 2_400).ToArray());
            line.Audio.Complete();
            backend.Play(line, 1f, _ => { }, _ => finished.TrySetResult(), (_, error) => throw error);
            await player.Played.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(DubLineState.Completed, line.State);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreparedAssetIsReadyBeforePlayAndIsReusedByDispatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-game-mixer-" + Guid.NewGuid().ToString("N"));
        var bridge = new TestResourceOverride();
        var player = new TestSoundPlayer();
        try
        {
            using var backend = new FfxivGameMixerAudioBackend(
                root, bridge, player, TimeSpan.Zero, TimeSpan.Zero);
            using var line = CreateLine();
            line.Audio.TryWrite(Enumerable.Repeat(0.1f, 2_400).ToArray());
            line.Audio.Complete();

            await backend.PrepareAsync(line, 1f, TestContext.Current.CancellationToken);
            Assert.True(line.PlaybackAssetReady);
            Assert.Equal(1, bridge.RegisterCount);
            Assert.Equal(0, player.PlayCount);

            backend.Play(line, 1f, _ => { }, _ => { }, (_, error) => throw error);
            await player.Played.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(1, bridge.RegisterCount);
            Assert.Equal(1, player.PlayCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SceneStopReleasesUnusedPreparedAssetButLineHandoffPreservesIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-game-mixer-" + Guid.NewGuid().ToString("N"));
        var bridge = new TestResourceOverride();
        try
        {
            using var backend = new FfxivGameMixerAudioBackend(
                root, bridge, new TestSoundPlayer(), TimeSpan.Zero, TimeSpan.Zero);
            using var line = CreateLine();
            line.Audio.TryWrite(new[] { 0.1f, 0.2f });
            line.Audio.Complete();
            await backend.PrepareAsync(line, 1f, TestContext.Current.CancellationToken);

            backend.Stop(discardPrepared: false);
            Assert.Equal(0, bridge.UnregisterCount);
            backend.Stop(discardPrepared: true);
            Assert.Equal(1, bridge.UnregisterCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResourceOverrideBecomingAvailableAfterConstructionRecoversBackend()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-game-mixer-" + Guid.NewGuid().ToString("N"));
        var bridge = new TestResourceOverride { Available = false };
        try
        {
            using var backend = new FfxivGameMixerAudioBackend(root, bridge, new TestSoundPlayer());
            Assert.False(backend.IsHealthy);

            bridge.Available = true;

            Assert.True(backend.IsAvailable);
            Assert.True(backend.IsHealthy);
            Assert.Equal("Resonance SCD resource override ready", backend.Diagnostic);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResourceOverrideFailurePoisonsSelectedBackendWithoutNaudioFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-game-mixer-" + Guid.NewGuid().ToString("N"));
        var bridge = new TestResourceOverride { RegisterSucceeds = false };
        var player = new TestSoundPlayer();
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var backend = new FfxivGameMixerAudioBackend(
                root, bridge, player, TimeSpan.Zero, TimeSpan.Zero);
            using var line = CreateLine();
            line.Audio.TryWrite(new[] { 0.1f, 0.2f });
            line.Audio.Complete();
            backend.Play(line, 1f, _ => { }, _ => { }, (_, error) => failed.TrySetResult(error));
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            Assert.False(backend.IsHealthy);
            Assert.Equal(0, player.PlayCount);
            Assert.Equal(DubLineState.Failed, line.State);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NativeDispatchRejectionFailsInsteadOfFallingBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-game-mixer-" + Guid.NewGuid().ToString("N"));
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var backend = new FfxivGameMixerAudioBackend(
                root, new TestResourceOverride(), new TestSoundPlayer { Reject = true },
                TimeSpan.Zero, TimeSpan.Zero);
            using var line = CreateLine();
            line.Audio.TryWrite(new[] { 0.1f, 0.2f });
            line.Audio.Complete();
            backend.Play(line, 1f, _ => { }, _ => Assert.Fail("Rejected playback completed"),
                (_, error) => failed.TrySetResult(error));

            var error = await failed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Contains("rejected", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(DubLineState.Failed, line.State);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationStopsAcceptedNativePlaybackByIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-game-mixer-" + Guid.NewGuid().ToString("N"));
        var player = new TestSoundPlayer();
        try
        {
            using var backend = new FfxivGameMixerAudioBackend(
                root, new TestResourceOverride(), player,
                TimeSpan.Zero, TimeSpan.Zero);
            using var line = CreateLine();
            line.Audio.TryWrite(Enumerable.Repeat(0.1f, 24_000).ToArray());
            line.Audio.Complete();
            backend.Play(line, 1f, _ => { }, _ => Assert.Fail("Cancelled playback completed"),
                (_, error) => throw error);
            await player.Played.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            backend.Stop();
            await player.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(new nint(1), player.StoppedHandle);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static DubLine CreateLine()
    {
        var line = new DubLine
        {
            SessionEpoch = 1,
            Sequence = 1,
            SpeakerKey = "test",
            SpeakerName = "Test",
            Text = "Test",
            ActualStatus = ActualStatus.Actual,
            NativeVoiceStatus = NativeVoiceStatus.NotVoiced,
        };
        Assert.True(line.TryTransition(DubLineState.Buffered, DubLineState.Predicted));
        return line;
    }

    private sealed class TestResourceOverride : IGameResourceOverride
    {
        public int RegisterCount { get; private set; }
        public int UnregisterCount { get; private set; }
        public bool RegisterSucceeds { get; init; } = true;
        public bool Available { get; set; } = true;
        public bool IsAvailable => Available;
        public string? UnavailableReason => Available ? null : "test resource override unavailable";
        public bool TryRegister(string virtualPath, string localPath, out string? error)
        {
            RegisterCount++;
            error = null;
            if (RegisterSucceeds) return true;
            error = "test resource override failure";
            return false;
        }
        public void Unregister(string virtualPath) => UnregisterCount++;
        public void Dispose() { }
    }

    private sealed class TestSoundPlayer : IGameMixerSoundPlayer
    {
        public int PlayCount;
        public bool Reject { get; init; }
        public nint StoppedHandle { get; private set; }
        public TaskCompletionSource Played { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<nint> PlayAsync(string virtualPath, CancellationToken token)
        {
            Interlocked.Increment(ref PlayCount);
            Played.TrySetResult();
            return Task.FromResult(Reject ? nint.Zero : new nint(1));
        }

        public Task StopAsync(nint playback)
        {
            StoppedHandle = playback;
            Stopped.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
