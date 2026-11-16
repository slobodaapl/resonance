using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Resonance.Game;
using Resonance.Scheduling;

namespace Resonance.Tests;

public sealed class LipSyncServiceTests
{
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)8)]
    public async Task LongSpeechKeepsMovingInCutsceneAndNormalModes(byte mode)
    {
        using var actor = new Actor();
        actor.Mode = mode;
        var framework = new DispatchQueue();
        var lips = new LipSyncService(framework, new ObjectTable(actor));
        using var line = Line(actor.Identity);
        lips.Start(line);
        framework.Drain();

        for (var frame = 0; frame < 600; frame++)
        {
            framework.Tick(TimeSpan.FromSeconds(1d / 60));
            actor.Advance(1f / 60);
            Assert.True(actor.MouthMoving, $"Mouth stopped at frame {frame} while speech remains active.");
            Assert.Equal(mode, actor.Mode);
        }

        lips.Stop(line);
        framework.Drain();
        actor.Advance(1f / 60);
        Assert.False(actor.MouthMoving);
        for (var frame = 0; frame < 180; frame++)
        {
            framework.Tick(TimeSpan.FromSeconds(1d / 60));
            actor.Advance(1f / 60);
            Assert.False(actor.MouthMoving);
        }
        await Dispose(lips, framework);
    }

    [Fact]
    public async Task SpeechKeepsTheMouthActiveWhenTheGameClearsItsAnimation()
    {
        using var actor = new Actor();
        var framework = new DispatchQueue();
        var lips = new LipSyncService(framework, new ObjectTable(actor));
        using var line = Line(actor.Identity);
        lips.Start(line);
        framework.Drain();

        for (var frame = 0; frame < 600; frame++)
        {
            actor.Timeline = 0;
            framework.Tick();
            Assert.Equal((ushort)630, actor.Timeline);
        }

        line.Cancel();
        framework.Tick();
        Assert.Equal((ushort)0, actor.Timeline);
        actor.Timeline = 777;
        framework.Tick();
        Assert.Equal((ushort)777, actor.Timeline);
        await Dispose(lips, framework);
    }

    [Fact]
    public async Task PreviousPlaybackCompletionCannotStopTheCurrentSpeaker()
    {
        using var actor = new Actor();
        var framework = new DispatchQueue();
        var lips = new LipSyncService(framework, new ObjectTable(actor));
        using var previous = Line(actor.Identity);
        using var current = Line(actor.Identity);
        lips.Start(previous);
        framework.Drain();
        lips.Start(current);
        framework.Drain();

        lips.Stop(previous);
        framework.Drain();
        Assert.Equal((ushort)630, actor.Timeline);

        lips.Stop(current);
        framework.Drain();
        Assert.Equal((ushort)0, actor.Timeline);
        framework.Tick();
        Assert.Equal((ushort)0, actor.Timeline);
        await Dispose(lips, framework);
        actor.Timeline = 777;
        framework.Tick();
        Assert.Equal((ushort)777, actor.Timeline);
    }

    [Theory]
    [InlineData(42u, 82ul, (byte)3)]
    [InlineData(43u, 81ul, (byte)3)]
    [InlineData(42u, 81ul, (byte)2)]
    public async Task UpdatesAndStopsCannotWriteToAnActorReusingTheAddress(uint baseId, ulong id, byte kind)
    {
        using var actor = new Actor();
        var framework = new DispatchQueue();
        var objects = new ObjectTable(actor);
        var lips = new LipSyncService(framework, objects);
        using var line = Line(actor.Identity);
        lips.Start(line);
        framework.RunNext();
        Assert.Equal((ushort)630, actor.Timeline);
        actor.BaseId = baseId;
        actor.GameObjectId = id;
        actor.ObjectKind = kind;
        actor.Timeline = 777;
        framework.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal((ushort)777, actor.Timeline);

        lips.Stop();
        framework.RunNext();
        Assert.Equal((ushort)777, actor.Timeline);
        lips.Start(line);
        framework.RunNext();
        Assert.Equal((ushort)777, actor.Timeline);

        await Dispose(lips, framework);
        Assert.Equal((ushort)777, actor.Timeline);
    }

    [Fact]
    public async Task DelayedStopCannotClearNewSpeechOnTheSameActor()
    {
        using var actor = new Actor();
        var framework = new DispatchQueue();
        var lips = new LipSyncService(framework, new ObjectTable(actor));
        using var line = Line(actor.Identity);
        lips.Start(line);
        framework.RunNext();
        lips.Start(line);
        framework.RunNext(index: 1);
        Assert.Equal((ushort)630, actor.Timeline);

        framework.RunNext();
        Assert.Equal((ushort)630, actor.Timeline);

        await Dispose(lips, framework);
        Assert.Equal((ushort)0, actor.Timeline);
    }

    [Fact]
    public async Task CancelledUpdateCannotReopenTheMouthAfterStop()
    {
        using var actor = new Actor();
        var framework = new DispatchQueue();
        var lips = new LipSyncService(framework, new ObjectTable(actor));
        using var line = Line(actor.Identity);
        lips.Start(line);
        lips.Stop();
        framework.RunNext(index: 1);
        framework.RunNext();
        Assert.Equal((ushort)0, actor.Timeline);
        await Dispose(lips, framework);
    }

    private static async Task Dispose(LipSyncService lips, DispatchQueue framework)
    {
        var task = lips.DisposeAsync().AsTask();
        framework.Drain();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static DubLine Line(DialogueActor actor) => new()
    {
        SessionEpoch = 1,
        Sequence = 1,
        SpeakerKey = actor.StableKey,
        SpeakerName = "Actor",
        Text = "Speech",
        Actor = actor,
    };

    private sealed class ObjectTable(ICharacter actor) : IObjectTable
    {
        public ICharacter? SearchById(ulong gameObjectId) =>
            actor.GameObjectId == gameObjectId ? actor : null;
    }

    private sealed class DispatchQueue : IFramework
    {
        public event IFramework.OnUpdateDelegate? Update;
        private readonly List<(Action Action, TaskCompletionSource Completion)> pending = [];
        public TimeSpan UpdateDelta { get; private set; }

        public void Tick(TimeSpan delta = default)
        {
            UpdateDelta = delta;
            Update?.Invoke(this);
        }

        public Task Run(Action action, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pending) pending.Add((action, completion));
            return completion.Task;
        }

        public void RunNext(int index = 0)
        {
            (Action Action, TaskCompletionSource Completion) item;
            lock (pending)
            {
                item = pending[index];
                pending.RemoveAt(index);
            }
            try { item.Action(); item.Completion.SetResult(); }
            catch (Exception error) { item.Completion.SetException(error); throw; }
        }

        public void Drain()
        {
            while (true)
            {
                lock (pending)
                {
                    if (pending.Count == 0) return;
                }
                RunNext();
            }
        }
    }

    private sealed unsafe class Actor : ICharacter, IDisposable
    {
        public uint BaseId { get; set; } = 42;
        public ulong GameObjectId { get; set; } = 81;
        public byte ObjectKind { get; set; } = 3;
        public nint Address { get; } = (nint)NativeMemory.AllocZeroed((nuint)sizeof(Character));
        public bool IsValid() => true;
        public DialogueActor Identity => new(BaseId, GameObjectId, Address, ObjectKind);
        public ushort Timeline
        {
            get => ((Character*)Address)->Timeline.Value;
            set => ((Character*)Address)->Timeline.Value = value;
        }
        public byte Mode
        {
            get => ((Character*)Address)->Mode;
            set => ((Character*)Address)->Mode = value;
        }
        public bool MouthMoving => ((Character*)Address)->Timeline.TimelineSequencer.MouthMoving;
        public void Advance(float seconds) => ((Character*)Address)->Advance(seconds);
        public void Dispose() => NativeMemory.Free((void*)Address);
    }
}
