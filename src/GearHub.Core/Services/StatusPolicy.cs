using GearHub.Core.Models;

namespace GearHub.Core.Services;

/// <summary>Правила перевода состояния устройства в цветовой статус.</summary>
public sealed class StatusPolicy
{
    /// <summary>Сколько времени после последнего контакта устройство считается «недавно отключённым» (жёлтым).</summary>
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
