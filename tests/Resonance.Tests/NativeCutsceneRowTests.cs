using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Resonance.Game;

namespace Resonance.Tests;

public sealed unsafe class NativeCutsceneRowTests
{
    [Theory]
    [InlineData(0x40)]
    [InlineData(0xb0)]
    public void ReadsThroughNativeAccessorDespiteInvalidLegacyResourceField(int resourceOffset)
    {
        var bytes = Resource();
        fixed (byte* buffer = bytes)
        {
            var resource = new ResourceBoundary { Data = buffer, Length = (uint)bytes.Length };
            byte* controller = stackalloc byte[0xb8];
            new Span<byte>(controller, 0xb8).Clear();
            *(nint*)(controller + 0x30) = unchecked((nint)0xA60000009C);
            *(nint*)(controller + resourceOffset) = (nint)(&resource);
            var row = NativeCutsceneRow.Read((nint)controller, resourceOffset, 856, (nint)(buffer + 0x18), &GetResourceData);
            Assert.Equal(new DialogueRow(856, 0x10, "TEXT_TEST_00001"), row);
            Assert.Equal(1, resource.Calls);
        }
    }

    [Fact]
    public void MissingControllerResourceDoesNotCallNativeAccessor()
    {
        var controller = new ControllerBoundary();
        Assert.Null(NativeCutsceneRow.Read((nint)(&controller), 0xb0, 856, 1, &GetResourceData));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void UnavailableOrOversizedResourceIsNotDereferenced(uint length)
    {
        var resource = new ResourceBoundary { Data = length == 0 ? null : (byte*)1, Length = length };
        var controller = new ControllerBoundary { Resource = (nint)(&resource) };
        Assert.Null(NativeCutsceneRow.Read((nint)(&controller), 0xb0, 856, 1, &GetResourceData));
        Assert.Equal(1, resource.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(0x60)]
    public void PayloadOutsideResourceIsRejected(int payloadOffset)
    {
        var bytes = Resource();
        fixed (byte* buffer = bytes)
        {
            var resource = new ResourceBoundary { Data = buffer, Length = (uint)bytes.Length };
            var controller = new ControllerBoundary { Resource = (nint)(&resource) };
            Assert.Null(NativeCutsceneRow.Read((nint)(&controller), 0xb0, 856, (nint)(buffer + payloadOffset), &GetResourceData));
        }
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("size")]
    [InlineData("key offset")]
    [InlineData("key prefix")]
    [InlineData("terminator")]
    public void MalformedRecordIsRejected(string fault)
    {
        var bytes = Resource();
        switch (fault)
        {
            case "tag": bytes[0x10] = (byte)'X'; break;
            case "size": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x14), 0x1000); break;
            case "key offset": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3c), int.MaxValue); break;
            case "key prefix": bytes[0x40] = (byte)'X'; break;
            case "terminator": bytes.AsSpan(0x40).Fill((byte)'A'); break;
        }
        fixed (byte* buffer = bytes)
        {
            var resource = new ResourceBoundary { Data = buffer, Length = (uint)bytes.Length };
            var controller = new ControllerBoundary { Resource = (nint)(&resource) };
            Assert.Null(NativeCutsceneRow.Read((nint)(&controller), 0xb0, 856, (nint)(buffer + 0x18), &GetResourceData));
        }
    }

    private static byte[] Resource()
    {
        var bytes = new byte[0x60];
        "C048"u8.CopyTo(bytes.AsSpan(0x10));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x14), 0x48);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3c), 0x28);
        Encoding.UTF8.GetBytes("TEXT_TEST_00001\0").CopyTo(bytes, 0x40);
        return bytes;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0xb8)]
    private struct ControllerBoundary
    {
        [FieldOffset(0xb0)] public nint Resource;
    }

    private struct ResourceBoundary
    {
        public byte* Data;
        public uint Length;
        public int Calls;
    }

    [UnmanagedCallersOnly]
    private static byte* GetResourceData(nint resource, uint* length)
    {
        var boundary = (ResourceBoundary*)resource;
        boundary->Calls++;
        *length = boundary->Length;
        return boundary->Data;
    }
}
