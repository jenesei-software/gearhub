using GearHub.Core.Persistence;

namespace GearHub.Core.Abstractions;

/// <summary>Store of last-seen device state and user ignore lists.</summary>
public interface IPresenceStore
{
    IReadOnlyCollection<GearPresenceRecord> GetAll();

    void Replace(IEnumerable<GearPresenceRecord> records);

    IReadOnlySet<string> IgnoredIds { get; }

    bool IsIgnored(string deviceId);

    void SetIgnored(string deviceId, bool ignored);
}
