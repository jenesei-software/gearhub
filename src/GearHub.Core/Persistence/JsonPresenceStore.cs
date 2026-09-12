using System.Text.Json;
using System.Text.Json.Serialization;
using GearHub.Core.Abstractions;
using GearHub.Core.Models;

namespace GearHub.Core.Persistence;

/// <summary>Документ файла состояния (формат <c>state.json</c>).</summary>
public sealed record PresenceStoreDocument
{
    public List<GearPresenceRecord> Records { get; init; } = [];

    public List<string> Ignored { get; init; } = [];
}

/// <summary>Простое JSON-хранилище с атомарной записью. Для MVP достаточно; при росте — заменить на SQLite.</summary>
public sealed class JsonPresenceStore : IPresenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, GearPresenceRecord> _records = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);

    public JsonPresenceStore(string path)
    {
        _path = path;
        Load();
    }

    public IReadOnlySet<string> IgnoredIds
    {
        get
        {
            lock (_gate)
            {
                return new HashSet<string>(_ignored, StringComparer.Ordinal);
            }
        }
    }

    public bool IsIgnored(string deviceId)
    {
        lock (_gate)
        {
            return _ignored.Contains(deviceId);
        }
    }

    public IReadOnlyCollection<GearPresenceRecord> GetAll()
    {
        lock (_gate)
        {
            return _records.Values.ToList();
        }
    }

    public void Replace(IEnumerable<GearPresenceRecord> records)
    {
        lock (_gate)
        {
            _records.Clear();
            foreach (var record in records)
            {
                _records[record.DeviceId] = record;
            }

            SaveLocked();
        }
    }

    public void SetIgnored(string deviceId, bool ignored)
    {
        lock (_gate)
        {
            if (ignored)
            {
                _ignored.Add(deviceId);
            }
            else
            {
                _ignored.Remove(deviceId);
            }

            SaveLocked();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var document = JsonSerializer.Deserialize<PresenceStoreDocument>(File.ReadAllText(_path), JsonOptions);
            if (document is null)
            {
                return;
            }

            foreach (var record in document.Records)
            {
                _records[record.DeviceId] = record;
            }

            foreach (var id in document.Ignored)
            {
                _ignored.Add(id);
            }
        }
        catch
        {
            // Повреждённый файл состояния не должен мешать запуску — начинаем с чистой истории.
        }
    }

    private void SaveLocked()
    {
        var document = new PresenceStoreDocument
        {
            Records = _records.Values.ToList(),
            Ignored = _ignored.ToList(),
        };

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
