using System.Runtime.InteropServices;
using Aegis.Core;

namespace Aegis;

public sealed record WindowsSystemInfo(
    string Platform,
    string OsVersion,
    int? Build,
    string Architecture) : IObservationData;

public sealed class WindowsSystemInfoObservationTool : IObservationTool
{
    public const string ToolId = "windows.system.info";

    public ObservationToolDescriptor Descriptor { get; } = new(
        ToolId,
        "Windows system information",
        "Reads the Windows platform, version, build, and architecture.");

    public Task<ObservationResult> ObserveAsync(
        ObservationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(request.ToolId, ToolId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The request does not target this observation tool.", nameof(request));
        }

        var version = Environment.OSVersion.Version;
        var data = new WindowsSystemInfo(
            "Windows",
            version.ToString(),
            version.Build >= 0 ? version.Build : null,
            RuntimeInformation.OSArchitecture.ToString());

        return Task.FromResult(new ObservationResult(
            request.RequestId,
            ToolId,
            DateTimeOffset.UtcNow,
            ObservationStatus.Succeeded,
            data));
    }
}
