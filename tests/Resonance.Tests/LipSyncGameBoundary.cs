// Only the framework, object table, and native animation write are substituted.
namespace Dalamud.Plugin.Services
{
    public interface IFramework
    {
        public delegate void OnUpdateDelegate(IFramework framework);
        event OnUpdateDelegate Update;
        TimeSpan UpdateDelta { get; }
        Task Run(Action action, CancellationToken cancellationToken = default);
    }

    public interface IObjectTable
    {
        Dalamud.Game.ClientState.Objects.Types.ICharacter? SearchById(ulong gameObjectId);
    }
}

namespace Dalamud.Game.ClientState.Objects.Types
{
    public interface ICharacter
    {
        uint BaseId { get; }
        ulong GameObjectId { get; }
        nint Address { get; }
        byte ObjectKind { get; }
        bool IsValid();
    }
}

namespace FFXIVClientStructs.FFXIV.Client.Game.Character
{
    public struct Character
    {
        public AnimationTimeline Timeline;
        public byte Mode;

        public void Advance(float seconds) => Timeline.Advance(seconds, Mode);
    }

    public struct AnimationTimeline
    {
        public ushort Value;
        public AnimationSequencer TimelineSequencer;

        public void SetLipsOverrideTimeline(ushort timeline)
        {
            Value = timeline;
            if (timeline == 0) TimelineSequencer.Stop();
        }

        public void Advance(float seconds, byte mode)
        {
            if (mode is 1 or 8 && Value != 0 && Value != TimelineSequencer.LipSlot)
                TimelineSequencer.PlayTimeline(Value);
            TimelineSequencer.Advance(seconds);
        }
    }

    public struct AnimationSequencer
    {
        public ushort LipSlot;
        public float ElapsedSeconds;
        public bool MouthMoving;

        public void PlayTimeline(ushort timeline)
        {
            LipSlot = timeline;
            ElapsedSeconds = 0;
            MouthMoving = true;
        }

        public void Stop()
        {
            LipSlot = 0;
            MouthMoving = false;
        }

        public void Advance(float seconds)
        {
            ElapsedSeconds += seconds;
            if (ElapsedSeconds >= 2) MouthMoving = false;
        }
    }
}
