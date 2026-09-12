using GearHub.Core.Persistence;

namespace GearHub.Core.Abstractions;

/// <summary>Хранилище «последний раз видели устройство» и пользовательских игнор-листов.</summary>
public interface IPresenceStore
{
    IReadOnlyCollection<GearPresenceRecord> GetAll();

    void Replace(IEnumerable<GearPresenceRecord> records);

    IReadOnlySet<string> IgnoredIds { get; }

    bool IsIgnored(string deviceId);

    void SetIgnored(string deviceId, bool ignored);
}
