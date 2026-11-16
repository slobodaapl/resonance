using Resonance.Game;

namespace Resonance.Tests;

public sealed class DialogueIdentityTests
{
    private static readonly DialogueActor Thordan = new(1012103, 17, 0x1000, 3);
    private static readonly DialogueActor Haurchefant = new(1006386, 18, 0x2000, 3);

    [Fact]
    public void QueuedRowsRetainTheirOwnNativeVoiceDespiteLaterSourceChanges()
    {
        var capture = new DialogueIdentity();
        var voice = new DialogueVoice(81, "cut/ex1/sound/voicem/native.scd");
        capture.Queue(1, 100, Thordan, new(856, 0x1a36c, "TEXT_THORDAN"), voice);
        capture.Dispatch(Haurchefant, () => capture.Present(9), new(856, 0x20000, "TEXT_HAURCHEFANT"));
        var source = capture.TakeQueued(1, 100)!.Value;
        capture.Dispatch(source.Actor, () => capture.Present(9), source.Row, source.NativeVoice);

        var actual = capture.Read(9)!;
        Assert.Equal("TEXT_THORDAN", actual.RowKey);
        Assert.Equal(Thordan, actual.Actor);
        Assert.Equal(voice, actual.NativeVoice);
        capture.Dispatch(null, () => capture.Present(9), new(856, 0x30000, "TEXT_UNDUBBED"));
        Assert.Null(capture.Read(9)!.NativeVoice);
    }

    [Theory]
    [InlineData(1012103u, 17ul, 0x1000, (byte)3, true)]
    [InlineData(1012103u, 18ul, 0x1000, (byte)3, false)]
    [InlineData(1012104u, 17ul, 0x1000, (byte)3, false)]
    [InlineData(1012103u, 17ul, 0x2000, (byte)3, false)]
    [InlineData(1012103u, 17ul, 0x1000, (byte)2, false)]
    public void ActorInstanceRejectsRecycledAddressesAndDifferentObjectKinds(
        uint baseId, ulong gameObjectId, int address, byte objectKind, bool expected) =>
        Assert.Equal(expected, Thordan.Matches(baseId, gameObjectId, address, objectKind));

    [Fact]
    public void EventAndBattleNpcIdsUseSeparateIdentityNamespaces()
    {
        var eventNpc = Thordan;
        var battleNpc = Thordan with { ObjectKind = 2 };
        Assert.Equal("npc:1012103", eventNpc.StableKey);
        Assert.Equal("bnpc:1012103", battleNpc.StableKey);
        Assert.Equal((uint)1012103, eventNpc.NpcBaseId);
        Assert.Null(battleNpc.NpcBaseId);
    }

    [Fact]
    public void QueuedActorsFollowTheirNativeEntriesRatherThanDisplayedNamesOrText()
    {
        var capture = new DialogueIdentity();
        capture.Queue(1, 100, Thordan);
        capture.Queue(1, 200, Haurchefant);
        var source = capture.TakeQueued(1, 100);
        capture.Dispatch(source?.Actor, () => capture.Present(9), source?.Row);
        var first = capture.Read(9)!;
        source = capture.TakeQueued(1, 200);
        capture.Dispatch(source?.Actor, () => capture.Present(9), source?.Row);
        var second = capture.Read(9)!;

        Assert.Equal(Thordan, first.Actor);
        Assert.Equal(Haurchefant, second.Actor);
        Assert.True(second.Sequence > first.Sequence);
        Assert.Null(capture.TakeQueued(1, 100));
    }

    [Fact]
    public void CancelledNativeEntriesCannotSurviveAddressReuse()
    {
        var capture = new DialogueIdentity();
        capture.Queue(1, 100, Thordan);
        capture.Queue(2, 200, Haurchefant);

        capture.RetainQueued(1, new HashSet<nint>());

        Assert.Null(capture.TakeQueued(1, 100));
        Assert.Equal(Haurchefant, capture.TakeQueued(2, 200)?.Actor);
    }

    [Fact]
    public void NativeEntryAddressesAreScopedToTheirQueueOwner()
    {
        var capture = new DialogueIdentity();
        capture.Queue(1, 100, Thordan);
        capture.Queue(2, 100, Haurchefant);
        Assert.Equal(Haurchefant, capture.TakeQueued(2, 100)?.Actor);
        Assert.Equal(Thordan, capture.TakeQueued(1, 100)?.Actor);
    }

    [Fact]
    public void ActorlessPresentationClearsPreviousActorAndAdvancesSequence()
    {
        var capture = new DialogueIdentity();
        capture.Dispatch(Thordan, () => capture.Present(9));
        var first = capture.Read(9)!;
        capture.Dispatch(null, () => capture.Present(9));
        Assert.Null(capture.Read(9)!.Actor);
        Assert.True(capture.Read(9)!.Sequence > first.Sequence);
    }

    [Fact]
    public void FinalizedAddonCannotRetainIdentityWhenReopened()
    {
        var capture = new DialogueIdentity();
        capture.Dispatch(Thordan, () => capture.Present(9));
        capture.Dispatch(Haurchefant, () => capture.Present(10));
        capture.Finalize(9);
        Assert.Null(capture.Read(9));
        Assert.Equal(Haurchefant, capture.Read(10)!.Actor);
        capture.Present(9);
        Assert.Null(capture.Read(9)!.Actor);
    }

    [Fact]
    public void ShutdownClearsQueuedAndPresentedActors()
    {
        var capture = new DialogueIdentity();
        capture.Dispatch(Thordan, () => capture.Present(9));
        capture.Queue(1, 100, Haurchefant);
        capture.Clear();
        Assert.Null(capture.Read(9));
        Assert.Null(capture.TakeQueued(1, 100));
    }

    [Fact]
    public void ReplacedActorAtSameAddressRetainsPerLineSnapshot()
    {
        var capture = new DialogueIdentity();
        capture.Dispatch(Thordan, () => capture.Present(9));
        var first = capture.Read(9)!;
        var replacement = Haurchefant with { Address = Thordan.Address };
        capture.Dispatch(replacement, () => capture.Present(9));
        Assert.Equal(Thordan, first.Actor);
        Assert.Equal(replacement, capture.Read(9)!.Actor);
    }

    [Fact]
    public void NestedNativeDispatchRestoresOuterActorWithoutLeakingItToLaterUiEvents()
    {
        var capture = new DialogueIdentity();
        capture.Dispatch(Thordan, () =>
        {
            capture.Dispatch(Haurchefant, () => capture.Present(10));
            capture.Present(9);
        });
        capture.Present(11);
        Assert.Equal(Thordan, capture.Read(9)!.Actor);
        Assert.Equal(Haurchefant, capture.Read(10)!.Actor);
        Assert.Null(capture.Read(11)!.Actor);
    }

    [Fact]
    public void FailedNativeDispatchCannotLeakActorIntoNextPresentation()
    {
        var capture = new DialogueIdentity();
        Assert.Throws<InvalidOperationException>(() =>
            capture.Dispatch(Thordan, () => throw new InvalidOperationException()));
        capture.Present(9);
        Assert.Null(capture.Read(9)!.Actor);
    }

    [Fact]
    public void PassiveUiRefreshDoesNotRestartTheSpokenLine()
    {
        var capture = new DialogueIdentity();
        capture.Dispatch(Thordan, () => capture.Present(9, "A long speech."));
        var original = capture.Read(9);

        capture.Present(9, "A long speech.");

        Assert.Same(original, capture.Read(9));
        capture.Present(9, "A different speech.");
        Assert.Null(capture.Read(9)!.Actor);
        Assert.True(capture.Read(9)!.Sequence > original!.Sequence);
    }

    [Fact]
    public void IdenticalTextFromANewNativeDispatchIsANewLine()
    {
        var capture = new DialogueIdentity();
        capture.Dispatch(Thordan, () => capture.Present(9, "Yes."));
        var original = capture.Read(9)!;
        capture.Dispatch(Haurchefant, () => capture.Present(9, "Yes."));
        Assert.Equal(Haurchefant, capture.Read(9)!.Actor);
        Assert.True(capture.Read(9)!.Sequence > original.Sequence);
    }
}
