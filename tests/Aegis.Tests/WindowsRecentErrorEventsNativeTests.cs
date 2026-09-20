using System.Reflection;
using System.Runtime.InteropServices;
using Aegis;
using Xunit;

namespace Aegis.Tests;

public sealed class WindowsRecentErrorEventsNativeTests
{
    [Fact]
    public void UsesWindowsEventLogRenderContextAndVariantDefinitions()
    {
        Assert.Equal(0, NativeWindowsRecentErrorEventsSampler.EvtRenderContextValues);
        Assert.Equal(0, NativeWindowsRecentErrorEventsSampler.EvtRenderEventValues);
        Assert.Equal(0x7f, NativeWindowsRecentErrorEventsSampler.EvtVariantTypeMask);
        Assert.Equal(0x80, NativeWindowsRecentErrorEventsSampler.EvtVariantTypeArray);
        Assert.Equal(1, NativeWindowsRecentErrorEventsSampler.EvtVariantTypeString);
        Assert.Equal(4, NativeWindowsRecentErrorEventsSampler.EvtVariantTypeUInt8);
        Assert.Equal(6, NativeWindowsRecentErrorEventsSampler.EvtVariantTypeUInt16);
        Assert.Equal(17, NativeWindowsRecentErrorEventsSampler.EvtVariantTypeFileTime);
        Assert.Equal(20, NativeWindowsRecentErrorEventsSampler.EvtVariantTypeHexInt32);

        var method = typeof(NativeWindowsRecentErrorEventsSampler).GetMethod(
            "EvtCreateRenderContext",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal(typeof(IntPtr), method!.GetParameters()[1].ParameterType);
    }

    [Fact]
    public void RejectedEventsStillConsumeThePerChannelRetrievalBudget()
    {
        var windowStart = DateTimeOffset.UtcNow.AddHours(-48);
        var windowEnd = DateTimeOffset.UtcNow;
        var returnedTimestamps = Enumerable
            .Repeat(windowStart.AddSeconds(-1), NativeWindowsRecentErrorEventsSampler.MaximumQueriedEventsPerChannel + 5)
            .ToArray();
        var retrievedEventCount = 0;
        var retainedEventCount = 0;

        while (NativeWindowsRecentErrorEventsSampler.HasRetrievalBudget(retrievedEventCount))
        {
            var occurredAt = returnedTimestamps[retrievedEventCount];
            retrievedEventCount++;
            if (occurredAt >= windowStart && occurredAt <= windowEnd)
            {
                retainedEventCount++;
            }
        }

        Assert.Equal(NativeWindowsRecentErrorEventsSampler.MaximumQueriedEventsPerChannel, retrievedEventCount);
        Assert.Equal(0, retainedEventCount);
    }

    [Fact]
    public void DecodesLevelEventIdAndTimeCreatedUsingTheirNativeScalarTypes()
    {
        var expectedTime = new DateTimeOffset(2026, 9, 20, 12, 34, 56, TimeSpan.Zero);

        var level = CreateVariant(
            NativeWindowsRecentErrorEventsSampler.EvtVariantTypeUInt8,
            variant => Marshal.WriteByte(variant, 2));
        var eventId = CreateVariant(
            NativeWindowsRecentErrorEventsSampler.EvtVariantTypeUInt16,
            variant => Marshal.WriteInt16(variant, 513));
        var timeCreated = CreateVariant(
            NativeWindowsRecentErrorEventsSampler.EvtVariantTypeFileTime,
            variant => Marshal.WriteInt64(variant, expectedTime.UtcDateTime.ToFileTimeUtc()));

        try
        {
            Assert.Equal(2, NativeWindowsRecentErrorEventsSampler.ReadUInt8Variant(level, 0));
            Assert.Equal(513, NativeWindowsRecentErrorEventsSampler.ReadEventIdVariant(eventId, 0));
            Assert.Equal(expectedTime, NativeWindowsRecentErrorEventsSampler.ReadFileTimeVariant(timeCreated, 0));
        }
        finally
        {
            Marshal.FreeHGlobal(level);
            Marshal.FreeHGlobal(eventId);
            Marshal.FreeHGlobal(timeCreated);
        }
    }

    [Fact]
    public void RejectsArrayVariantsForEveryScalarReader()
    {
        var providerPointer = Marshal.StringToCoTaskMemUni("Provider");
        var provider = CreateVariant(
            NativeWindowsRecentErrorEventsSampler.EvtVariantTypeString |
                NativeWindowsRecentErrorEventsSampler.EvtVariantTypeArray,
            variant => Marshal.WriteIntPtr(variant, providerPointer));
        var level = CreateVariant(
            NativeWindowsRecentErrorEventsSampler.EvtVariantTypeUInt8 |
                NativeWindowsRecentErrorEventsSampler.EvtVariantTypeArray,
            variant => Marshal.WriteByte(variant, 1));
        var eventId = CreateVariant(
            NativeWindowsRecentErrorEventsSampler.EvtVariantTypeUInt16 |
                NativeWindowsRecentErrorEventsSampler.EvtVariantTypeArray,
            variant => Marshal.WriteInt16(variant, 1));
        var timeCreated = CreateVariant(
            NativeWindowsRecentErrorEventsSampler.EvtVariantTypeFileTime |
                NativeWindowsRecentErrorEventsSampler.EvtVariantTypeArray,
            variant => Marshal.WriteInt64(variant, DateTime.UtcNow.ToFileTimeUtc()));

        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                NativeWindowsRecentErrorEventsSampler.ReadStringVariant(provider, 0));
            Assert.Throws<InvalidOperationException>(() =>
                NativeWindowsRecentErrorEventsSampler.ReadUInt8Variant(level, 0));
            Assert.Throws<InvalidOperationException>(() =>
                NativeWindowsRecentErrorEventsSampler.ReadEventIdVariant(eventId, 0));
            Assert.Throws<InvalidOperationException>(() =>
                NativeWindowsRecentErrorEventsSampler.ReadFileTimeVariant(timeCreated, 0));
        }
        finally
        {
            Marshal.FreeCoTaskMem(providerPointer);
            Marshal.FreeHGlobal(provider);
            Marshal.FreeHGlobal(level);
            Marshal.FreeHGlobal(eventId);
            Marshal.FreeHGlobal(timeCreated);
        }
    }

    [Fact]
    public void RejectsUnsupportedVariantTypes()
    {
        var level = CreateVariant(
            NativeWindowsRecentErrorEventsSampler.EvtVariantTypeUInt16,
            variant => Marshal.WriteInt16(variant, 1));
        var eventId = CreateVariant(
            NativeWindowsRecentErrorEventsSampler.EvtVariantTypeFileTime,
            variant => Marshal.WriteInt64(variant, DateTime.UtcNow.ToFileTimeUtc()));
        var timeCreated = CreateVariant(
            NativeWindowsRecentErrorEventsSampler.EvtVariantTypeUInt8,
            variant => Marshal.WriteByte(variant, 1));

        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                NativeWindowsRecentErrorEventsSampler.ReadUInt8Variant(level, 0));
            Assert.Throws<InvalidOperationException>(() =>
                NativeWindowsRecentErrorEventsSampler.ReadEventIdVariant(eventId, 0));
            Assert.Throws<InvalidOperationException>(() =>
                NativeWindowsRecentErrorEventsSampler.ReadFileTimeVariant(timeCreated, 0));
        }
        finally
        {
            Marshal.FreeHGlobal(level);
            Marshal.FreeHGlobal(eventId);
            Marshal.FreeHGlobal(timeCreated);
        }
    }

    private static IntPtr CreateVariant(int type, Action<IntPtr> writeValue)
    {
        var variant = Marshal.AllocHGlobal(16);
        Marshal.WriteInt32(variant, 12, type);
        writeValue(variant);
        return variant;
    }
}
