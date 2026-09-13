using GearHub.Core.Models;

namespace GearHub.Core.Services;

/// <summary>Rules mapping device state to a color status.</summary>
public sealed class StatusPolicy
{
    /// <summary>How long after the last contact a device is considered "recently offline" (yellow).</summary>
    public TimeSpan RecentlyOfflineWindow { get; init; } = TimeSpan.FromHours(6);

    public GearStatus Evaluate(bool isConnected, bool hasFault, DateTimeOffset lastSeenUtc, DateTimeOffset nowUtc)
    {
        if (isConnected)
        {
            return hasFault ? GearStatus.Attention : GearStatus.Online;
        }

        var offlineFor = nowUtc - lastSeenUtc;
        return offlineFor <= RecentlyOfflineWindow ? GearStatus.Attention : GearStatus.Lost;
    }
}
