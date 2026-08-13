using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Resonance.Bootstrap;
using Resonance.Audio;
using Resonance.Plugin;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class BaseRuntimeHostProtocolTests
{
    [Fact]
    public async Task FramedHostMessagesRoundTripWithExactRequestAndPayload()
    {
        var requestId = Guid.NewGuid().ToString("N");
        var expectedPayload = BaseHostProtocol.SerializePayload(new { backend = "cpu", sequence = 7 });
        var frame = new BaseHostFrame(
            BaseHostProtocol.SchemaVersion,
            ReferenceExtractionProtocol.AbiVersion,
            BaseHostFrameKind.Ping,
            requestId,
            expectedPayload,
            1);
        await using var stream = new MemoryStream();

        await BaseHostProtocol.WriteFrameAsync(stream, frame, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var roundTrip = await BaseHostProtocol.ReadFrameAsync(stream, TestContext.Current.CancellationToken);

        Assert.NotNull(roundTrip);
        Assert.Equal(requestId, roundTrip!.RequestId);
        Assert.Equal(BaseHostFrameKind.Ping, roundTrip.Kind);
        Assert.Equal(expectedPayload, roundTrip.Payload);
    }

    [Fact]
    public void CancelOperationPayloadPreservesTargetRequestAndKind()
    {
        var target = Guid.NewGuid().ToString("N");
        var encoded = BaseHostProtocol.SerializePayload(
            new BaseHostCancelPayload(target, BaseHostFrameKind.Benchmark));

        var decoded = BaseHostProtocol.DeserializePayload<BaseHostCancelPayload>(encoded);

        Assert.Equal(target, decoded.TargetRequestId);
        Assert.Equal(BaseHostFrameKind.Benchmark, decoded.TargetKind);
    }

    [Fact]
    public void HostFrameRejectsOversizedPayloadBeforeTransport()
    {
        var frame = new BaseHostFrame(
            BaseHostProtocol.SchemaVersion,
            ReferenceExtractionProtocol.AbiVersion,
            BaseHostFrameKind.Ping,
            Guid.NewGuid().ToString("N"),
            new string('x', BaseHostProtocol.MaximumFrameBytes + 1),
            1);

        Assert.Throws<InvalidDataException>(() => BaseHostProtocol.ValidateFrame(frame));
    }

    [Fact]
    public void HostCommandSequenceRejectsOldIdsAfterHighWaterMark()
    {
        var sequence = new BaseHostCommandSequence();

        Assert.True(sequence.TryAccept(100_000));
        Assert.False(sequence.TryAccept(1));
        Assert.False(sequence.TryAccept(100_000));
        Assert.True(sequence.TryAccept(100_001));
    }

    [Fact]
    public void ResponseRouterRejectsControlOverflowAndReportsFatalFailure()
    {
        var failures = 0;
        var router = new BaseHostResponseRouter(1, 1, _ => failures++);

        Assert.True(router.TryQueueControl(Frame(BaseHostFrameKind.Pong)));
        Assert.False(router.TryQueueControl(Frame(BaseHostFrameKind.Pong)));
        Assert.True(router.IsFailed);
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task ResponseRouterPrioritizesControlOverAlreadyQueuedAudio()
    {
        var router = new BaseHostResponseRouter(2, 2);

        Assert.True(router.TryQueueAudio(Frame(BaseHostFrameKind.Audio)));
        Assert.True(router.TryQueueControl(Frame(BaseHostFrameKind.Completed)));

        Assert.Equal(BaseHostFrameKind.Completed,
            (await router.ReadNextAsync(TestContext.Current.CancellationToken))!.Kind);
        Assert.Equal(BaseHostFrameKind.Audio,
            (await router.ReadNextAsync(TestContext.Current.CancellationToken))!.Kind);
        router.Complete();
        Assert.Null(await router.ReadNextAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ResponseRouterAudioOverflowInvokesNativeCancellationHook()
    {
        var cancellationRequests = 0;
        var router = new BaseHostResponseRouter(1, 1,
            audioFailure: _ => cancellationRequests++);

        Assert.True(router.TryQueueAudio(Frame(BaseHostFrameKind.Audio)));
        Assert.False(router.TryQueueAudio(Frame(BaseHostFrameKind.Audio)));
        Assert.Equal(1, cancellationRequests);
    }

    [Fact]
    public void VoiceReferenceValidationRejectsMalformedShapeValuesAndOverflow()
    {
        Assert.Throws<InvalidDataException>(() => BaseHostProtocol.ValidateVoiceReferencePayload(
            new BaseHostReferencePayload(null!, [1], 1, 1, "line")));
        Assert.Throws<InvalidDataException>(() => BaseHostProtocol.ValidateVoiceReferencePayload(
            new BaseHostReferencePayload([0.1f], null!, 1, 1, "line")));
        Assert.Throws<InvalidDataException>(() => BaseHostProtocol.ValidateVoiceReferencePayload(
            new BaseHostReferencePayload([0.1f], [1], 1, 1, null!)));
        Assert.Throws<InvalidDataException>(() => BaseHostProtocol.ValidateVoiceReferencePayload(
            new BaseHostReferencePayload([float.NaN], [1], 1, 1, "line")));
        Assert.Throws<InvalidDataException>(() => BaseHostProtocol.ValidateVoiceReferencePayload(
            new BaseHostReferencePayload([float.PositiveInfinity], [1], 1, 1, "line")));
        Assert.Throws<InvalidDataException>(() => BaseHostProtocol.ValidateVoiceReferencePayload(
            new BaseHostReferencePayload([0.1f], [-1], 1, 1, "line")));
        Assert.Throws<InvalidDataException>(() => BaseHostProtocol.ValidateVoiceReferencePayload(
            new BaseHostReferencePayload([0.1f], [1], 2, 1, "line")));
        Assert.Throws<InvalidDataException>(() => BaseHostProtocol.ValidateVoiceReferencePayload(
            new BaseHostReferencePayload(new float[BaseHostProtocol.MaximumSpeakerEmbeddingValues + 1],
                [1], 1, 1, "line")));
        Assert.Throws<InvalidDataException>(() => BaseHostProtocol.ValidateVoiceReferenceShape(
            1, ReferenceExtractionProtocol.MaximumSamples, BaseHostProtocol.MaximumCodebooks));
    }

    [Fact]
    public void FailedBenchmarkResultRoundTripsWithNullableMetrics()
    {
        var encoded = BaseHostProtocol.SerializePayload(new BaseHostBenchmarkResult(
            "cuda", false, null, null, null, "backend initialization failed"));

        var decoded = BaseHostProtocol.DeserializePayload<BaseHostBenchmarkResult>(encoded);

        Assert.False(decoded.Successful);
        Assert.Null(decoded.InitializationSeconds);
        Assert.Null(decoded.TimeToFirstAudioSeconds);
        Assert.Null(decoded.RealTimeFactor);
        Assert.Equal("backend initialization failed", decoded.Error);
    }

    [Fact]
    public void BenchmarkResponseCarriesAuthoritativeContextState()
    {
        var encoded = BaseHostProtocol.SerializePayload(new BaseHostBenchmarkResponse(
            [new BaseHostBenchmarkResult("cpu", false, 0.1, null, null, "init failed")],
            false, null));

        var decoded = BaseHostProtocol.DeserializePayload<BaseHostBenchmarkResponse>(encoded);

        Assert.False(decoded.ContextReady);
        Assert.Null(decoded.ActiveBackendId);
        Assert.Single(decoded.Results);
    }

    [Fact]
    public async Task ProductionHostServerProcessesPingDuplicateAndShutdownFrames()
    {
        await using var input = await InputStreamAsync(
            Command(BaseHostFrameKind.Ping, 1),
            Command(BaseHostFrameKind.Ping, 1),
            Command(BaseHostFrameKind.Shutdown, 2));
        await using var output = new MemoryStream();
        var runtime = new FakeServerRuntime();
        using var server = new Resonance.ReferenceExtractor.BaseHostServer(
            runtime, input, output);

        await server.RunAsync();

        output.Position = 0;
        var kinds = new List<BaseHostFrameKind>();
        while (await BaseHostProtocol.ReadFrameAsync(output,
                   TestContext.Current.CancellationToken) is { } frame)
            kinds.Add(frame.Kind);
        Assert.Contains(BaseHostFrameKind.Ready, kinds);
        Assert.Contains(BaseHostFrameKind.Failed, kinds);
        Assert.Contains(BaseHostFrameKind.ShutdownAck, kinds);
        Assert.True(runtime.ShutdownCount > 0);
    }

    [Fact]
    public async Task ProductionHostServerCleanEofCancelsFakeNativeRuntime()
    {
        await using var input = await InputStreamAsync(Command(BaseHostFrameKind.Ping, 1));
        await using var output = new MemoryStream();
        var runtime = new FakeServerRuntime();
        using var server = new Resonance.ReferenceExtractor.BaseHostServer(
            runtime, input, output);

        await server.RunAsync();

        Assert.True(runtime.ShutdownCount > 0);
    }

    [Fact]
    public async Task ProductionHostServerAudioBackpressureCancelsFakeNativeRequest()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var commandPayload = BaseHostProtocol.SerializePayload(new BaseHostSynthesisPayload(
            "A line.", "english", null, "A calm voice.", 1, 64));
        await using var input = await InputStreamAsync(
            CommandWithId(BaseHostFrameKind.Synthesize, operationId, 1, commandPayload));
        await using var gatedInput = new GateInputStream(input.ToArray());
        await using var output = new FrameSignalOutputStream(
            frame => frame.Kind == BaseHostFrameKind.Failed
                     && frame.RequestId == operationId,
            blockWrites: true);
        var runtime = new FakeServerRuntime { EmitManyChunks = true };
        using var server = new Resonance.ReferenceExtractor.BaseHostServer(
            runtime, gatedInput, output);

        var run = server.RunAsync();
        await runtime.CancelObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        output.Release();
        await output.FrameObserved.WaitAsync(TestContext.Current.CancellationToken);
        gatedInput.ReleaseEof();
        await run;

        Assert.True(runtime.CancelCount > 0);
    }

    [Fact]
    public async Task CompletedAudioDisposeDoesNotCancelRuntime()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var payload = BaseHostProtocol.SerializePayload(new BaseHostSynthesisPayload(
            "A line.", "english", null, "A calm voice.", 1, 64));
        await using var source = await InputStreamAsync(
            CommandWithId(BaseHostFrameKind.Synthesize, operationId, 1, payload));
        await using var input = new GateInputStream(source.ToArray());
        await using var output = new FrameSignalOutputStream(
            frame => frame.Kind == BaseHostFrameKind.Completed
                     && frame.RequestId == operationId);
        var runtime = new FakeServerRuntime();
        using var server = new Resonance.ReferenceExtractor.BaseHostServer(
            runtime, input, output);

        var run = server.RunAsync();
        await runtime.SynthesisCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await output.FrameObserved.WaitAsync(TestContext.Current.CancellationToken);
        input.ReleaseEof();
        await run;
        server.Dispose();

        Assert.Equal(0, runtime.CancelCount);
    }

    [Fact]
    public async Task OpenAudioDisposeCancelsRuntimeExactlyOnce()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var payload = BaseHostProtocol.SerializePayload(new BaseHostSynthesisPayload(
            "A line.", "english", null, "A calm voice.", 1, 64));
        await using var source = await InputStreamAsync(
            CommandWithId(BaseHostFrameKind.Synthesize, operationId, 1, payload));
        await using var input = new GateInputStream(source.ToArray());
        await using var output = new MemoryStream();
        var runtime = new FakeServerRuntime { BlockSynthesis = true };
        using var server = new Resonance.ReferenceExtractor.BaseHostServer(
            runtime, input, output);

        var run = server.RunAsync();
        await runtime.SynthesisStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        server.Dispose();
        await run;

        Assert.Equal(1, runtime.CancelCount);
    }

    [Fact]
    public async Task ProductionHostServerDoesNotDispatchQueuedExtractionAfterCleanEof()
    {
        var payload = BaseHostProtocol.SerializePayload(
            new BaseHostExtractPayload("/tmp/reference.f32", "A reference line."));
        await using var source = await InputStreamAsync(
            CommandWithPayload(BaseHostFrameKind.Extract, 1, payload));
        await using var input = new GateInputStream(source.ToArray());
        await using var output = new MemoryStream();
        var runtime = new FakeServerRuntime();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new Resonance.ReferenceExtractor.BaseHostServer(
            runtime, input, output, _ =>
            {
                entered.TrySetResult();
                return release.Task;
            });

        var run = server.RunAsync();
        await entered.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        input.ReleaseEof();
        await runtime.ShutdownObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        release.TrySetResult();
        await run;

        Assert.Equal(0, runtime.ExtractCount);
    }

    [Theory]
    [InlineData(BaseHostFrameKind.Synthesize)]
    [InlineData(BaseHostFrameKind.Benchmark)]
    [InlineData(BaseHostFrameKind.SwitchBackend)]
    public async Task ProductionHostServerCancelsQueuedOperationByExactKind(
        BaseHostFrameKind operationKind)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var operationPayload = operationKind switch
        {
            BaseHostFrameKind.Synthesize => BaseHostProtocol.SerializePayload(
                new BaseHostSynthesisPayload("A line.", "english", null,
                    "A calm voice.", 1, 64)),
            BaseHostFrameKind.Benchmark => BaseHostProtocol.SerializePayload(
                new BaseHostBenchmarkPayload(["cpu"])),
            BaseHostFrameKind.SwitchBackend => BaseHostProtocol.SerializePayload("cpu"),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind)),
        };
        var cancelId = Guid.NewGuid().ToString("N");
        var cancelPayload = BaseHostProtocol.SerializePayload(
            new BaseHostCancelPayload(operationId, operationKind));
        await using var source = await InputStreamAsync(
            CommandWithId(operationKind, operationId, 1, operationPayload),
            CommandWithId(BaseHostFrameKind.CancelOperation, cancelId, 2, cancelPayload));
        await using var input = new GateInputStream(source.ToArray());
        await using var output = new FrameSignalOutputStream(
            frame => frame.Kind == BaseHostFrameKind.Failed
                     && frame.RequestId == operationId);
        var runtime = new FakeServerRuntime();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationHandled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new Resonance.ReferenceExtractor.BaseHostServer(
            runtime, input, output, frame =>
            {
                if (frame.RequestId != operationId) return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            },
            afterCancellation: frame =>
            {
                if (frame.RequestId == cancelId) cancellationHandled.TrySetResult();
                return Task.CompletedTask;
            });

        var run = server.RunAsync();
        await entered.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellationHandled.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        release.TrySetResult();
        await runtime.DispatchObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await output.FrameObserved.WaitAsync(TestContext.Current.CancellationToken);
        input.ReleaseEof();
        await run;

        output.Position = 0;
        var frames = new List<BaseHostFrame>();
        while (await BaseHostProtocol.ReadFrameAsync(output,
                   TestContext.Current.CancellationToken) is { } frame)
            frames.Add(frame);
        var failure = frames.Single(frame => frame.Kind == BaseHostFrameKind.Failed
                                             && frame.RequestId == operationId);
        var error = BaseHostProtocol.DeserializePayload<HostFailure>(failure.Payload);
        Assert.True(error.Canceled);
        Assert.Contains(frames, frame => frame.Kind == BaseHostFrameKind.Failed
                                         && frame.RequestId == cancelId);
        Assert.Equal(0, operationKind switch
        {
            BaseHostFrameKind.Synthesize => runtime.SynthesizeCount,
            BaseHostFrameKind.Benchmark => runtime.BenchmarkCount,
            BaseHostFrameKind.SwitchBackend => runtime.SwitchBackendCount,
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind)),
        });
    }

    [Fact]
    public async Task ProductionHostServerForwardsCancellationToActiveSynthesis()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var cancelId = Guid.NewGuid().ToString("N");
        var payload = BaseHostProtocol.SerializePayload(
            new BaseHostSynthesisPayload("A line.", "english", null,
                "A calm voice.", 1, 64));
        var cancelPayload = BaseHostProtocol.SerializePayload(
            new BaseHostCancelPayload(operationId, BaseHostFrameKind.Synthesize));
        await using var firstCommand = await InputStreamAsync(
            CommandWithId(BaseHostFrameKind.Synthesize, operationId, 1, payload));
        await using var cancelCommand = await InputStreamAsync(
            CommandWithId(BaseHostFrameKind.CancelOperation, cancelId, 2, cancelPayload));
        var firstBytes = firstCommand.ToArray();
        var allBytes = firstBytes.Concat(cancelCommand.ToArray()).ToArray();
        await using var input = new GateInputStream(allBytes, firstBytes.Length);
        await using var output = new FrameSignalOutputStream(
            frame => frame.Kind == BaseHostFrameKind.Failed
                     && frame.RequestId == operationId);
        var runtime = new FakeServerRuntime { BlockSynthesis = true };
        using var server = new Resonance.ReferenceExtractor.BaseHostServer(
            runtime, input, output);

        var run = server.RunAsync();
        await runtime.SynthesisStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        input.ReleaseNext();
        await runtime.CancelObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await output.FrameObserved.WaitAsync(TestContext.Current.CancellationToken);
        input.ReleaseEof();
        await run;

        output.Position = 0;
        var frames = new List<BaseHostFrame>();
        while (await BaseHostProtocol.ReadFrameAsync(output,
                   TestContext.Current.CancellationToken) is { } frame)
            frames.Add(frame);
        Assert.Contains(frames, frame => frame.Kind == BaseHostFrameKind.Failed
                                         && frame.RequestId == operationId);
    }

    [Fact]
    public async Task ProductionHostServerRejectsWrongKindCancellationWithoutCancellingQueuedRequest()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var cancelId = Guid.NewGuid().ToString("N");
        var payload = BaseHostProtocol.SerializePayload(
            new BaseHostSynthesisPayload("A line.", "english", null,
                "A calm voice.", 1, 64));
        var cancelPayload = BaseHostProtocol.SerializePayload(
            new BaseHostCancelPayload(operationId, BaseHostFrameKind.Benchmark));
        await using var source = await InputStreamAsync(
            CommandWithId(BaseHostFrameKind.Synthesize, operationId, 1, payload),
            CommandWithId(BaseHostFrameKind.CancelOperation, cancelId, 2, cancelPayload));
        await using var input = new GateInputStream(source.ToArray());
        await using var output = new FrameSignalOutputStream(
            frame => frame.Kind == BaseHostFrameKind.Completed
                     && frame.RequestId == operationId);
        var runtime = new FakeServerRuntime();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new Resonance.ReferenceExtractor.BaseHostServer(
            runtime, input, output, frame =>
            {
                if (frame.RequestId != operationId) return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            });

        var run = server.RunAsync();
        await entered.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        release.TrySetResult();
        await runtime.SynthesisCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await output.FrameObserved.WaitAsync(TestContext.Current.CancellationToken);
        input.ReleaseEof();
        await run;

        output.Position = 0;
        var frames = new List<BaseHostFrame>();
        while (await BaseHostProtocol.ReadFrameAsync(output,
                   TestContext.Current.CancellationToken) is { } frame)
            frames.Add(frame);
        Assert.Contains(frames, frame => frame.Kind == BaseHostFrameKind.Failed
                                         && frame.RequestId == cancelId);
        Assert.Contains(frames, frame => frame.Kind == BaseHostFrameKind.Completed
                                         && frame.RequestId == operationId);
        Assert.Equal(1, runtime.SynthesizeCount);
        Assert.Equal(0, runtime.CancelCount);
    }

    [Fact]
    public async Task ProductionHostServerRejectsWrongKindCancellationBeforeTargetArrives()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var cancelId = Guid.NewGuid().ToString("N");
        var operationPayload = BaseHostProtocol.SerializePayload(
            new BaseHostSynthesisPayload("A line.", "english", null,
                "A calm voice.", 1, 64));
        var cancelPayload = BaseHostProtocol.SerializePayload(
            new BaseHostCancelPayload(operationId, BaseHostFrameKind.Benchmark));
        await using var source = await InputStreamAsync(
            CommandWithId(BaseHostFrameKind.CancelOperation, cancelId, 1, cancelPayload),
            CommandWithId(BaseHostFrameKind.Synthesize, operationId, 2, operationPayload));
        await using var input = new GateInputStream(source.ToArray());
        await using var output = new FrameSignalOutputStream(
            frame => frame.Kind == BaseHostFrameKind.Completed
                     && frame.RequestId == operationId);
        var runtime = new FakeServerRuntime();
        using var server = new Resonance.ReferenceExtractor.BaseHostServer(
            runtime, input, output);

        var run = server.RunAsync();
        await runtime.SynthesisCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await output.FrameObserved.WaitAsync(TestContext.Current.CancellationToken);
        input.ReleaseEof();
        await run;

        output.Position = 0;
        var frames = new List<BaseHostFrame>();
        while (await BaseHostProtocol.ReadFrameAsync(output,
                   TestContext.Current.CancellationToken) is { } frame)
            frames.Add(frame);
        Assert.Contains(frames, frame => frame.Kind == BaseHostFrameKind.Failed
                                         && frame.RequestId == cancelId);
        Assert.Contains(frames, frame => frame.Kind == BaseHostFrameKind.Completed
                                         && frame.RequestId == operationId);
        Assert.Equal(1, runtime.SynthesizeCount);
        Assert.Equal(0, runtime.CancelCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProductionHostServerPublishesReservationBeforeInvocationForCancellation(
        bool matchingCancellation)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var cancelId = Guid.NewGuid().ToString("N");
        var operationPayload = BaseHostProtocol.SerializePayload(
            new BaseHostSynthesisPayload("A line.", "english", null,
                "A calm voice.", 1, 64));
        var targetKind = matchingCancellation
            ? BaseHostFrameKind.Synthesize
            : BaseHostFrameKind.Benchmark;
        var cancelPayload = BaseHostProtocol.SerializePayload(
            new BaseHostCancelPayload(operationId, targetKind));
        await using var source = await InputStreamAsync(
            CommandWithId(BaseHostFrameKind.Synthesize, operationId, 1, operationPayload),
            CommandWithId(BaseHostFrameKind.CancelOperation, cancelId, 2, cancelPayload));
        await using var input = new GateInputStream(source.ToArray());
        await using var output = new FrameSignalOutputStream(
            frame => frame.RequestId == operationId
                     && (matchingCancellation
                         ? frame.Kind == BaseHostFrameKind.Failed
                         : frame.Kind == BaseHostFrameKind.Completed));
        var runtime = new FakeServerRuntime();
        var reservationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReservation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationHandled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new Resonance.ReferenceExtractor.BaseHostServer(
            runtime, input, output,
            afterReservation: frame =>
            {
                if (frame.RequestId != operationId) return Task.CompletedTask;
                reservationEntered.TrySetResult();
                return releaseReservation.Task;
            },
            afterCancellation: frame =>
            {
                if (frame.RequestId == cancelId) cancellationHandled.TrySetResult();
                return Task.CompletedTask;
            });

        var run = server.RunAsync();
        await reservationEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellationHandled.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (matchingCancellation)
            await runtime.CancelObserved.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        releaseReservation.TrySetResult();
        if (!matchingCancellation)
            await runtime.SynthesisCompleted.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await output.FrameObserved.WaitAsync(TestContext.Current.CancellationToken);
        input.ReleaseEof();
        await run;

        output.Position = 0;
        var frames = new List<BaseHostFrame>();
        while (await BaseHostProtocol.ReadFrameAsync(output,
                   TestContext.Current.CancellationToken) is { } frame)
            frames.Add(frame);
        Assert.Contains(frames, frame => frame.Kind == BaseHostFrameKind.Failed
                                         && frame.RequestId == cancelId);
        if (matchingCancellation)
        {
            Assert.Contains(frames, frame => frame.Kind == BaseHostFrameKind.Failed
                                             && frame.RequestId == operationId);
            Assert.Equal(0, runtime.SynthesizeCount);
            Assert.True(runtime.CancelCount > 0);
        }
        else
        {
            Assert.Contains(frames, frame => frame.Kind == BaseHostFrameKind.Completed
                                             && frame.RequestId == operationId);
            Assert.Equal(1, runtime.SynthesizeCount);
            Assert.Equal(0, runtime.CancelCount);
        }
    }

    [Fact]
    public async Task BuiltHelperHostFramingSmokeUsesProductionProtocol()
    {
        var helperCandidate = Environment.GetEnvironmentVariable("RESONANCE_TEST_HELPER")
            ?? new[]
            {
                Path.Combine(AppContext.BaseDirectory, "reference-extractor", "ReferenceExtractor.exe"),
                Path.Combine(AppContext.BaseDirectory, "reference-extractor", "ReferenceExtractor.dll"),
            }.FirstOrDefault(File.Exists);
        var runtimeDirectory = Environment.GetEnvironmentVariable("RESONANCE_TEST_RUNTIME_DIR");
        var talkerPath = Environment.GetEnvironmentVariable("RESONANCE_TEST_TALKER");
        var codecPath = Environment.GetEnvironmentVariable("RESONANCE_TEST_CODEC");
        if (String.IsNullOrWhiteSpace(helperCandidate)
            || String.IsNullOrWhiteSpace(runtimeDirectory)
            || String.IsNullOrWhiteSpace(talkerPath)
            || String.IsNullOrWhiteSpace(codecPath))
            Assert.Skip("Helper framing smoke requires RESONANCE_TEST_HELPER, RESONANCE_TEST_RUNTIME_DIR, RESONANCE_TEST_TALKER, and RESONANCE_TEST_CODEC.");
        var helper = helperCandidate ?? throw new InvalidOperationException("Helper path was not provided");
        var runtime = runtimeDirectory ?? throw new InvalidOperationException("Runtime path was not provided");
        var talker = talkerPath ?? throw new InvalidOperationException("Talker path was not provided");
        var codec = codecPath ?? throw new InvalidOperationException("Codec path was not provided");
        if (!File.Exists(helper) || !Directory.Exists(runtime)
            || !File.Exists(talker) || !File.Exists(codec))
            Assert.Skip("Configured helper/native model paths are unavailable in this environment.");

        var root = CreateRoot();
        Process? child = null;
        try
        {
            var nonce = Guid.NewGuid().ToString("N");
            var hostRoot = Path.Combine(root, "host");
            Directory.CreateDirectory(hostRoot);
            var modelRoot = Path.GetDirectoryName(talker)
                ?? throw new InvalidOperationException("Configured talker path has no model root");
            var helperRoot = Path.GetDirectoryName(helper)
                ?? throw new InvalidOperationException("Configured helper path has no helper root");
            var requestPath = Path.Combine(hostRoot, "request.json");
            var request = new BaseHostLaunchRequest(
                BaseHostProtocol.SchemaVersion, ReferenceExtractionProtocol.AbiVersion,
                Path.GetFullPath(runtime), Path.GetFullPath(talker),
                Path.GetFullPath(codec),
                Environment.GetEnvironmentVariable("RESONANCE_TEST_BACKEND") ?? "cpu",
                Path.GetFullPath(runtime), Path.GetFullPath(modelRoot),
                Path.GetFullPath(hostRoot), Path.GetFullPath(root), nonce,
                Path.GetFullPath(helperRoot));
            await File.WriteAllTextAsync(requestPath,
                JsonSerializer.Serialize(request, BaseHostProtocol.JsonOptions()),
                TestContext.Current.CancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = helper.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? "dotnet" : helper,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            if (startInfo.FileName == "dotnet") startInfo.ArgumentList.Add(helper);
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add(requestPath);
            child = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Configured helper process did not start");
            await File.WriteAllTextAsync(Path.Combine(hostRoot, "launch.ready"), nonce,
                TestContext.Current.CancellationToken);

            var input = child.StandardInput.BaseStream;
            var firstPing = Command(BaseHostFrameKind.Ping, 1);
            var duplicatePing = firstPing with { Sequence = 1 };
            var malformedCancel = CommandWithPayload(BaseHostFrameKind.CancelSynthesis, 2, "null");
            var shutdown = Command(BaseHostFrameKind.Shutdown, 3);
            await BaseHostProtocol.WriteFrameAsync(input, firstPing,
                TestContext.Current.CancellationToken);
            await BaseHostProtocol.WriteFrameAsync(input, duplicatePing,
                TestContext.Current.CancellationToken);
            await BaseHostProtocol.WriteFrameAsync(input, malformedCancel,
                TestContext.Current.CancellationToken);
            await BaseHostProtocol.WriteFrameAsync(input, shutdown,
                TestContext.Current.CancellationToken);
            await input.FlushAsync(TestContext.Current.CancellationToken);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var frames = new List<BaseHostFrame>();
            while (frames.Count < 5)
            {
                var frame = await BaseHostProtocol.ReadFrameAsync(
                    child.StandardOutput.BaseStream, timeout.Token);
                if (frame is null) break;
                frames.Add(frame);
            }
            var kinds = frames.Select(frame => frame.Kind).ToArray();
            Assert.Contains(BaseHostFrameKind.Ready, kinds);
            Assert.Contains(BaseHostFrameKind.Pong, kinds);
            Assert.Contains(BaseHostFrameKind.ShutdownAck, kinds);
            Assert.Contains(frames, frame => frame.Kind == BaseHostFrameKind.Failed
                                             && frame.RequestId == duplicatePing.RequestId);
            Assert.Contains(frames, frame => frame.Kind == BaseHostFrameKind.Failed
                                             && frame.RequestId == malformedCancel.RequestId);
            await child.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (child is not null)
            {
                try
                {
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    child.Dispose();
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                catch (NotSupportedException) { }
            }
            TestDirectory.Delete(root);
        }
    }

    private static BaseHostFrame Command(BaseHostFrameKind kind, long sequence) => new(
        BaseHostProtocol.SchemaVersion, ReferenceExtractionProtocol.AbiVersion, kind,
        Guid.NewGuid().ToString("N"), "{}", sequence);

    private static BaseHostFrame CommandWithPayload(
        BaseHostFrameKind kind, long sequence, string payload) => new(
        BaseHostProtocol.SchemaVersion, ReferenceExtractionProtocol.AbiVersion, kind,
        Guid.NewGuid().ToString("N"), payload, sequence);

    private static BaseHostFrame CommandWithId(
        BaseHostFrameKind kind, string requestId, long sequence, string payload) => new(
        BaseHostProtocol.SchemaVersion, ReferenceExtractionProtocol.AbiVersion, kind,
        requestId, payload, sequence);

    private static async Task<MemoryStream> InputStreamAsync(params BaseHostFrame[] frames)
    {
        var stream = new MemoryStream();
        foreach (var frame in frames)
            await BaseHostProtocol.WriteFrameAsync(stream, frame,
                TestContext.Current.CancellationToken);
        stream.Position = 0;
        return stream;
    }

    private static BaseHostFrame Frame(BaseHostFrameKind kind) => new(
        BaseHostProtocol.SchemaVersion, ReferenceExtractionProtocol.AbiVersion, kind,
        Guid.NewGuid().ToString("N"), "{}", kind is BaseHostFrameKind.Ping ? 1 : 0);

    [Fact]
    public async Task ResidentHostServesReferenceAndSynthesisWithoutReloadingBase()
    {
        var root = CreateRoot();
        try
        {
            var host = new FakeHost();
            await using var manager = CreateManager(root, new Configuration
            {
                KeepBaseModelLoaded = true,
            }, host);

            await manager.EnsureReadyAsync(TestContext.Current.CancellationToken);
            _ = await manager.ExtractReferenceAsync(
                new float[ReferenceExtractionProtocol.SampleRate], "A reference sentence.",
                TestContext.Current.CancellationToken);
            using var output = new StreamingAudioBuffer();
            await manager.SynthesizeAsync(
                new("A short line.", "english", null, "A calm voice.", 7), output,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, host.StartCount);
            Assert.Equal(1, host.ExtractCount);
            Assert.Equal(1, host.SynthesizeCount);
            Assert.Equal(0, host.DisposeCount);
        }
        finally { TestDirectory.Delete(root); }
    }

    [Fact]
    public async Task NonResidentHostStartsAndExitsForEachBaseRequest()
    {
        var root = CreateRoot();
        try
        {
            var host = new FakeHost();
            await using var manager = CreateManager(root, new Configuration(), host);

            await manager.EnsureReadyAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, host.StartCount);

            using (var first = new StreamingAudioBuffer())
            {
                await manager.SynthesizeAsync(
                    new("First line.", "english", null, "A calm voice.", 1), first,
                    TestContext.Current.CancellationToken);
            }
            using (var second = new StreamingAudioBuffer())
            {
                await manager.SynthesizeAsync(
                    new("Second line.", "english", null, "A calm voice.", 2), second,
                    TestContext.Current.CancellationToken);
            }

            Assert.Equal(2, host.StartCount);
            Assert.Equal(2, host.DisposeCount);
        }
        finally { TestDirectory.Delete(root); }
    }

    [Fact]
    public async Task CutsceneResidencyLeaseKeepsNonResidentBaseAcrossAdjacentLines()
    {
        var root = CreateRoot();
        try
        {
            var host = new FakeHost();
            await using var manager = CreateManager(root, new Configuration(), host);
            await manager.EnsureReadyAsync(TestContext.Current.CancellationToken);
            manager.AcquireBaseResidencyLease();

            using (var first = new StreamingAudioBuffer())
                await manager.SynthesizeAsync(
                    new("First line.", "english", null, "A calm voice.", 1), first,
                    TestContext.Current.CancellationToken);
            using (var second = new StreamingAudioBuffer())
                await manager.SynthesizeAsync(
                    new("Second line.", "english", null, "A calm voice.", 2), second,
                    TestContext.Current.CancellationToken);

            Assert.Equal(1, host.StartCount);
            Assert.Equal(0, host.DisposeCount);
            await manager.ReleaseBaseResidencyLeaseAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, host.DisposeCount);
        }
        finally { TestDirectory.Delete(root); }
    }

    [Fact]
    public async Task BusyBaseHostIsReportedWithoutVoiceDesignFallback()
    {
        var root = CreateRoot();
        try
        {
            var host = new FakeHost { ThrowBusyOnSynthesis = true };
            await using var manager = CreateManager(root, new Configuration(), host);
            using var output = new StreamingAudioBuffer();

            await Assert.ThrowsAsync<BaseRuntimeHostBusyException>(() => manager.SynthesizeAsync(
                new("A line that must remain Base.", "english", null, null, 3), output,
                TestContext.Current.CancellationToken));

            Assert.Equal(1, host.SynthesizeCount);
            Assert.Equal(1, host.DisposeCount);
        }
        finally { TestDirectory.Delete(root); }
    }

    [Fact]
    public async Task AliveBaseHostWithUnreadyContextIsInitializedBeforeSynthesis()
    {
        var root = CreateRoot();
        try
        {
            var host = new FakeHost();
            host.SetState(isReady: true, contextReady: false, activeBackend: null);
            await using var manager = CreateManager(root, new Configuration(), host);
            manager.SetTestBaseHost(host);
            using var output = new StreamingAudioBuffer();

            await manager.SynthesizeAsync(
                new("A line after context recovery.", "english", null, null, 4), output,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, host.SwitchCount);
            Assert.Equal(1, host.SynthesizeCount);
        }
        finally { TestDirectory.Delete(root); }
    }

    [Fact]
    public async Task AliveBaseHostWithWrongBackendIsSwitchedBeforeSynthesis()
    {
        var root = CreateRoot();
        try
        {
            var host = new FakeHost();
            host.SetState(isReady: true, contextReady: true, activeBackend: "other");
            await using var manager = CreateManager(root, new Configuration(), host);
            manager.SetTestBaseHost(host);
            using var output = new StreamingAudioBuffer();

            await manager.SynthesizeAsync(
                new("A line after backend recovery.", "english", null, null, 6), output,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, host.SwitchCount);
            Assert.Equal(1, host.SynthesizeCount);
        }
        finally { TestDirectory.Delete(root); }
    }

    [Fact]
    public async Task AliveBaseHostWithUnreadyContextIsInitializedBeforeExtraction()
    {
        var root = CreateRoot();
        try
        {
            var host = new FakeHost();
            host.SetState(isReady: true, contextReady: false, activeBackend: null);
            await using var manager = CreateManager(root, new Configuration(), host);
            manager.SetTestBaseHost(host);

            _ = await manager.ExtractReferenceAsync(
                new float[ReferenceExtractionProtocol.SampleRate], "A reference line.",
                TestContext.Current.CancellationToken);

            Assert.Equal(1, host.SwitchCount);
            Assert.Equal(1, host.ExtractCount);
        }
        finally { TestDirectory.Delete(root); }
    }

    [Fact]
    public async Task FailedBaseHostContextRecoveryDoesNotSynthesize()
    {
        var root = CreateRoot();
        try
        {
            var host = new FakeHost { FailSwitchBackend = true };
            host.SetState(isReady: true, contextReady: false, activeBackend: null);
            await using var manager = CreateManager(root, new Configuration(), host);
            manager.SetTestBaseHost(host);
            using var output = new StreamingAudioBuffer();

            await Assert.ThrowsAsync<BaseRuntimeHostException>(() => manager.SynthesizeAsync(
                new("A line without context.", "english", null, null, 5), output,
                TestContext.Current.CancellationToken));

            Assert.Equal(1, host.SwitchCount);
            Assert.Equal(0, host.SynthesizeCount);
        }
        finally { TestDirectory.Delete(root); }
    }

    private static RuntimeManager CreateManager(string root, Configuration configuration, FakeHost host)
    {
        var runtime = Path.Combine(root, "runtime");
        var work = Path.Combine(root, "reference-extraction");
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(work);
        var talker = Path.Combine(root, "base.gguf");
        var codec = Path.Combine(root, "codec.gguf");
        var helper = Path.Combine(root, "ReferenceExtractor.exe");
        File.WriteAllText(talker, "base");
        File.WriteAllText(codec, "codec");
        File.WriteAllText(helper, "host");
        var manager = new RuntimeManager(configuration, () => { }, talker, codec,
            "base", "design", "runtime", runtime, helper, work);
        manager.SetBaseHostFactory((_, _, _, _, _, _) => host);
        var backend = new BackendInfo("cpu", "CPU", BackendType.Cpu, 0, 1, 1);
        manager.SetTestBackendState(new BackendSelection(backend, backend, false, null));
        return manager;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-base-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FakeHost : IBaseRuntimeHost
    {
        public bool IsReady { get; private set; }
        public bool ContextReady { get; private set; }
        public string? ActiveBackendId { get; private set; }
        public bool IsBusy { get; private set; }
        public int StartCount { get; private set; }
        public int SwitchCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int ExtractCount { get; private set; }
        public int SynthesizeCount { get; private set; }
        public bool ThrowBusyOnSynthesis { get; init; }
        public bool FailSwitchBackend { get; init; }

        public void SetState(bool isReady, bool contextReady, string? activeBackend)
        {
            IsReady = isReady;
            ContextReady = contextReady;
            ActiveBackendId = activeBackend;
        }

        public Task StartAsync(string backend, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            StartCount++;
            IsReady = true;
            ContextReady = true;
            ActiveBackendId = backend;
            return Task.CompletedTask;
        }

        public Task SwitchBackendAsync(string backend, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            SwitchCount++;
            if (FailSwitchBackend)
                throw new BaseRuntimeHostException("fake backend context recovery failed");
            ContextReady = true;
            ActiveBackendId = backend;
            return Task.CompletedTask;
        }

        public Task<VoiceReference> ExtractReferenceAsync(
            ReadOnlyMemory<float> _, string transcript, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ExtractCount++;
            return Task.FromResult(new VoiceReference([0.1f], [1], 1, 1, transcript));
        }

        public async Task SynthesizeAsync(SynthesisRequest _, StreamingAudioBuffer sink,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            SynthesizeCount++;
            if (ThrowBusyOnSynthesis)
                throw new BaseRuntimeHostBusyException("Base extraction is active.");
            IsBusy = true;
            try
            {
                await sink.WriteAsync(new float[240], token);
                sink.Complete();
            }
            finally { IsBusy = false; }
        }

        public Task<IReadOnlyList<BackendBenchmarkMeasurement>> BenchmarkAsync(
            IReadOnlyList<BackendInfo> _, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<BackendBenchmarkMeasurement>>([]);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            IsReady = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeServerRuntime : Resonance.ReferenceExtractor.IBaseHostServerRuntime
    {
        private int extraction;
        private int synthesis;
        private int extractCount;
        private int synthesizeCount;
        private int benchmarkCount;
        private int switchBackendCount;
        private int shutdownCount;
        private int cancelCount;
        private readonly ConcurrentDictionary<string, byte> canceledOperations = new(StringComparer.Ordinal);
        public int ShutdownCount => Volatile.Read(ref shutdownCount);
        public int CancelCount => Volatile.Read(ref cancelCount);
        public int ExtractCount => Volatile.Read(ref extractCount);
        public int SynthesizeCount => Volatile.Read(ref synthesizeCount);
        public int BenchmarkCount => Volatile.Read(ref benchmarkCount);
        public int SwitchBackendCount => Volatile.Read(ref switchBackendCount);
        public bool EmitManyChunks { get; init; }
        public bool BlockSynthesis { get; init; }
        public TaskCompletionSource CancelObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DispatchObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SynthesisStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SynthesisCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ShutdownObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string BackendName => "fake";
        public bool IsTerminalPoisoned => false;
        public bool ContextReady => true;
        public string? ActiveBackendId => BackendName;
        public bool ExtractionActive => Volatile.Read(ref extraction) != 0;
        public bool SynthesisActive => Volatile.Read(ref synthesis) != 0;

        public bool TryBeginExtraction() => Interlocked.CompareExchange(ref extraction, 1, 0) == 0;
        public void EndExtraction() => Volatile.Write(ref extraction, 0);
        public bool TryBeginSynthesis(string _) => Interlocked.CompareExchange(ref synthesis, 1, 0) == 0;
        public void EndSynthesis(string _) => Volatile.Write(ref synthesis, 0);
        public void CancelSynthesis(string _)
        {
            Volatile.Write(ref synthesis, 0);
            Interlocked.Increment(ref cancelCount);
            CancelObserved.TrySetResult();
        }
        public void CancelOperation(string requestId, BaseHostFrameKind targetKind)
        {
            canceledOperations[requestId] = 0;
            Interlocked.Increment(ref cancelCount);
            CancelObserved.TrySetResult();
            if (targetKind == BaseHostFrameKind.Synthesize)
                Volatile.Write(ref synthesis, 0);
        }
        public bool IsOperationCancellationRequested(string requestId) =>
            canceledOperations.ContainsKey(requestId);
        public void ClearOperationCancellation(string requestId)
        {
            canceledOperations.TryRemove(requestId, out _);
            DispatchObserved.TrySetResult();
        }
        public void Shutdown()
        {
            Interlocked.Increment(ref shutdownCount);
            ShutdownObserved.TrySetResult();
        }
        public BaseHostReferencePayload Extract(BaseHostExtractPayload payload) =>
            ExtractCore(payload);

        private BaseHostReferencePayload ExtractCore(BaseHostExtractPayload payload)
        {
            Interlocked.Increment(ref extractCount);
            return new([0.1f], [1], 1, 1, payload.Transcript);
        }

        public void Synthesize(BaseHostSynthesisPayload _, string requestId,
            Resonance.ReferenceExtractor.AudioSender sendAudio)
        {
            Interlocked.Increment(ref synthesizeCount);
            try
            {
                if (BlockSynthesis)
                {
                    SynthesisStarted.TrySetResult();
                    CancelObserved.Task.GetAwaiter().GetResult();
                }
                var count = EmitManyChunks ? 64 : 1;
                for (var index = 0; index < count && sendAudio(requestId, new float[32]); index++) { }
            }
            finally { SynthesisCompleted.TrySetResult(); }
        }

        public void SwitchBackend(string _, string __) => Interlocked.Increment(ref switchBackendCount);
        public IReadOnlyList<BaseHostBenchmarkResult> Benchmark(
            IReadOnlyList<string> _, string __)
        {
            Interlocked.Increment(ref benchmarkCount);
            return [];
        }
        public void Dispose() { }
    }

    private sealed record HostFailure(
        string Message,
        bool Canceled = false,
        bool? ContextReady = null,
        string? ActiveBackendId = null);

    private sealed class GateInputStream(byte[] input, long? releaseAt = null) : Stream
    {
        private readonly MemoryStream source = new(input, writable: false);
        private readonly TaskCompletionSource eof =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource next =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object boundaryGate = new();
        private long? releaseBoundary = releaseAt;

        public void ReleaseEof() => eof.TrySetResult();
        public void ReleaseNext()
        {
            lock (boundaryGate) releaseBoundary = null;
            next.TrySetResult();
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => source.Read(buffer);
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            long? boundary;
            lock (boundaryGate) boundary = releaseBoundary;
            if (boundary is { } boundaryValue && source.Position >= boundaryValue)
            {
                await next.Task.WaitAsync(cancellationToken);
                lock (boundaryGate) boundary = releaseBoundary;
            }
            if (source.Position < source.Length)
            {
                var count = buffer.Length;
                if (boundary is { } boundaryLimit)
                    count = (int)Math.Min(count, Math.Max(0, boundaryLimit - source.Position));
                if (count > 0)
                    return await source.ReadAsync(buffer[..count], cancellationToken);
            }
            await eof.Task.WaitAsync(cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FrameSignalOutputStream : Stream
    {
        private readonly MemoryStream storage = new();
        private readonly Func<BaseHostFrame, bool> predicate;
        private readonly TaskCompletionSource frameObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource? release;
        private readonly object streamGate = new();
        private int parseOffset;

        public FrameSignalOutputStream(
            Func<BaseHostFrame, bool> predicate, bool blockWrites = false)
        {
            this.predicate = predicate;
            if (blockWrites)
                release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task FrameObserved => frameObserved.Task;
        public void Release() => release?.TrySetResult();
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length { get { lock (streamGate) return storage.Length; } }
        public override long Position
        {
            get { lock (streamGate) return storage.Position; }
            set { lock (streamGate) storage.Position = value; }
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (streamGate)
            {
                storage.Write(buffer);
                ObserveFramesLocked();
            }
        }
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copy = buffer.ToArray();
            if (release is not null)
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Write(copy);
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (streamGate) return storage.Read(buffer, offset, count);
        }
        public override int Read(Span<byte> buffer)
        {
            lock (streamGate) return storage.Read(buffer);
        }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (streamGate) return new(storage.Read(buffer.Span));
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (streamGate) return storage.Seek(offset, origin);
        }
        public override void SetLength(long value)
        {
            lock (streamGate) storage.SetLength(value);
        }

        private void ObserveFramesLocked()
        {
            var bytes = storage.ToArray();
            while (bytes.Length - parseOffset >= sizeof(int))
            {
                var payloadLength = BitConverter.ToInt32(bytes, parseOffset);
                if (payloadLength <= 0 || payloadLength > BaseHostProtocol.MaximumFrameBytes)
                    return;
                if (bytes.Length - parseOffset < sizeof(int) + payloadLength)
                    return;
                try
                {
                    var frame = JsonSerializer.Deserialize<BaseHostFrame>(
                        bytes.AsSpan(parseOffset + sizeof(int), payloadLength),
                        BaseHostProtocol.JsonOptions());
                    if (frame is not null && predicate(frame))
                        frameObserved.TrySetResult();
                }
                catch (JsonException) { return; }
                parseOffset += sizeof(int) + payloadLength;
            }
        }
    }
}
