using Aegis;
using Xunit;

namespace Aegis.Tests;

public sealed class PresentationFormattingTests
{
    [Fact]
    public void FormatsPercentagesWithHumanPrecision()
    {
        Assert.Equal("12.3%", PresentationFormatting.FormatPercentage(12.34));
    }

    [Fact]
    public void FormatsEvidenceStepIdsForDisplayWithoutChangingTheirMeaning()
    {
        Assert.Equal("step 2", PresentationFormatting.FormatEvidenceStepId("step2"));
        Assert.Equal("step 10", PresentationFormatting.FormatEvidenceStepId("step10"));
        Assert.Equal("step-2", PresentationFormatting.FormatEvidenceStepId("step-2"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("s", "s")]
    [InlineData("step", "step")]
    [InlineData("stepx", "stepx")]
    public void LeavesNullShortAndMalformedEvidenceStepIdsUnchanged(string? stepId, string? expected)
    {
        Assert.Equal(expected, PresentationFormatting.FormatEvidenceStepId(stepId));
    }

    [Fact]
    public void FormatsByteQuantitiesUsingReadableBinaryUnits()
    {
        Assert.Equal("1.0 GiB", PresentationFormatting.FormatBytes(1024UL * 1024 * 1024));
        Assert.Equal("512.0 MiB", PresentationFormatting.FormatBytes(512UL * 1024 * 1024));
        Assert.Equal("4.0 KiB", PresentationFormatting.FormatBytes(4UL * 1024));
        Assert.Equal("42 B", PresentationFormatting.FormatBytes(42));
    }

    [Fact]
    public void FormatsPersistedTimestampsAsLocalDateAndTimeWithoutAnOffset()
    {
        var timestamp = new DateTimeOffset(2026, 9, 29, 1, 3, 4, TimeSpan.Zero);

        var formatted = PresentationFormatting.FormatLocalDateTime(timestamp);

        Assert.Equal(timestamp.ToLocalTime().ToString("g"), formatted);
        Assert.DoesNotContain("+00:00", formatted, StringComparison.Ordinal);
    }
}
