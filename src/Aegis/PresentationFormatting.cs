using System.Globalization;

namespace Aegis;

public static class PresentationFormatting
{
    public static string FormatPercentage(double value) => $"{value:F1}%";

    public static string? FormatEvidenceStepId(string? stepId)
    {
        if (stepId is null || !stepId.StartsWith("step", StringComparison.OrdinalIgnoreCase))
        {
            return stepId;
        }

        var suffix = stepId.AsSpan(4);
        return suffix.Length > 0 &&
               suffix[0] >= '0' &&
               suffix[0] <= '9' &&
               int.TryParse(suffix, out var stepNumber)
            ? $"step {stepNumber}"
            : stepId;
    }

    public static string FormatLocalDateTime(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public static string FormatBytes(ulong bytes)
    {
        const double kibibyte = 1024;
        const double mebibyte = kibibyte * 1024;
        const double gibibyte = mebibyte * 1024;
        return bytes switch
        {
            >= (ulong)gibibyte => $"{bytes / gibibyte:F1} GiB",
            >= (ulong)mebibyte => $"{bytes / mebibyte:F1} MiB",
            >= (ulong)kibibyte => $"{bytes / kibibyte:F1} KiB",
            _ => $"{bytes} B"
        };
    }
}
