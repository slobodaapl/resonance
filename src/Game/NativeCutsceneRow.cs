using System.Buffers.Binary;
using System.Text;

namespace Resonance.Game;

internal static unsafe class NativeCutsceneRow
{
    internal static DialogueRow? Read(nint controller, int resourceOffset, uint cutsceneId, nint data,
        delegate* unmanaged<nint, uint*, byte*> getResourceData)
    {
        var resource = *(nint*)(controller + resourceOffset);
        if (resource == 0) return null;
        uint length = 0;
        var buffer = getResourceData(resource, &length);
        if (buffer == null || length > Int32.MaxValue) return null;
        var payload = data - (nint)buffer;
        if (payload < 8 || payload > (long)length - 0x28) return null;
        var bytes = new ReadOnlySpan<byte>(buffer, (int)length);
        var offset = (int)payload - 8;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]) != 0x38343043) return null;
        var size = BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 4)..]);
        if (size < 0x30 || size > bytes.Length - offset) return null;
        var keyOffset = (long)payload + BinaryPrimitives.ReadInt32LittleEndian(bytes[((int)payload + 0x24)..]);
        if (keyOffset < 0 || keyOffset >= bytes.Length) return null;
        var keyBytes = bytes[(int)keyOffset..];
        var end = keyBytes.IndexOf((byte)0);
        if (end < 0) return null;
        var key = Encoding.UTF8.GetString(keyBytes[..end]);
        return key.StartsWith("TEXT_", StringComparison.Ordinal)
            ? new(cutsceneId, offset, key) : null;
    }
}
