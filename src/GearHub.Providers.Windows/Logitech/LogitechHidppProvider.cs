using System.Collections.Concurrent;
using System.Text;
using GearHub.Core.Abstractions;
using GearHub.Core.Models;
using GearHub.Core.Services;

namespace GearHub.Providers.Windows.Logitech;

/// <summary>
/// Logitech через HID++.
/// Основной канал — уведомления: приёмник рассылает connection-нотификации (sub_id 0x41) о спаренных
/// устройствах, а сами устройства присылают события о заряде (sub_id 0x07/0x0D). Уведомления приходят
/// даже когда устройство «спит» и не отвечает на прямые запросы, поэтому они и являются источником истины.
/// Опрос (HID++ 2.0: 0x1004/0x1001/0x1000, HID++ 1.0: регистры 0x0D/0x07) выполняется периодически,
/// когда устройство доступно, и уточняет заряд и имя.
/// </summary>
public sealed class LogitechHidppProvider : IGearProvider, IDisposable
{
    /// <summary>SoftwareId как в Solaar: старший бит установлен, чтобы отличать ответы от нотификаций (swId = 0).</summary>
    private const byte SoftwareId = 0x0B;

    private const byte DeviceIndexReceiver = 0xFF;
    private const byte RootFeatureIndex = 0x00;
    private const byte SubIdConnectionNotification = 0x41;

    private const ushort RegisterReceiverConnection = 0x02;
    private const ushort RegisterNotifications = 0x00;
    private const ushort RegisterInfoRequest = 0x83B5;

    private const ushort FeatureDeviceName = 0x0005;
    private const ushort FeatureBatteryVoltage = 0x1000;
    private const ushort FeatureBatteryStatus = 0x1001;
    private const ushort FeatureUnifiedBattery = 0x1004;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan NameReadInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BatteryReadInterval = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan NotificationWindow = TimeSpan.FromMilliseconds(4000);
    private static readonly TimeSpan NotificationReadTimeout = TimeSpan.FromMilliseconds(150);

    private readonly List<HidppTransport> _transports = [];
    private readonly ConcurrentDictionary<string, LiveDevice> _devices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _attempts = new(StringComparer.Ordinal);

    public string ProviderName => "Logitech HID++";

    public Task<IReadOnlyList<GearObservation>> DiscoverAsync(CancellationToken cancellationToken)
        => Task.Run(() => (IReadOnlyList<GearObservation>)Scan(cancellationToken), cancellationToken);

    public void Dispose()
    {
        lock (_transports)
        {
            foreach (var transport in _transports)
            {
                transport.Dispose();
            }

            _transports.Clear();
        }
    }

    private List<GearObservation> Scan(CancellationToken cancellationToken)
    {
        EnsureTransports();
        var now = DateTimeOffset.UtcNow;
        var results = new List<GearObservation>();

        foreach (var transport in _transports)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (transport.LooksLikeReceiver)
                {
                    // Просим приёмник разослать актуальную информацию о связи с устройствами.
                    _ = transport.TryExchange(
                        WriteRegister(DeviceIndexReceiver, RegisterReceiverConnection, [0x02]),
                        DeviceIndexReceiver,
                        ProbeTimeout,
                        out _);
                }
                else
                {
                    TrySeedDirectDevice(transport);
                }
            }
            catch
            {
            }
        }

        // После «дёрга» приёмники присылают нотификации о связи и заряде — собираем их.
        CollectNotifications(NotificationWindow);

        foreach (var live in _devices.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                RefreshLiveDevice(live, now);

                var observation = BuildObservation(live);
                if (observation is not null)
                {
                    results.Add(observation);
                }
            }
            catch
            {
                // Одно устройство не должно ломать весь скан.
            }
        }

        return results;
    }

    /// <summary>Устройство, подключённое напрямую (Bluetooth/кабель): уведомлений приёмника для него нет.</summary>
    private void TrySeedDirectDevice(HidppTransport transport)
    {
        var key = DeviceKey(transport, HidppCodec.DeviceIndexDirect);
        if (_devices.ContainsKey(key))
        {
            return;
        }

        var live = _devices.GetOrAdd(key, _ => new LiveDevice
        {
            Transport = transport,
            DeviceIndex = HidppCodec.DeviceIndexDirect,
            Name = transport.ProductName,
        });

        var probe = TryReadViaFeature(live);
        if (probe is null || !probe.Value.Battery.IsAvailable)
        {
            _devices.TryRemove(key, out _);
            return;
        }

        live.Battery = probe.Value.Battery;
        live.BatteryDetail = probe.Value.Detail;
    }

    private void RefreshLiveDevice(LiveDevice live, DateTimeOffset now)
    {
        if (!live.NotificationsEnabled && TryEnableBatteryNotifications(live))
        {
            live.NotificationsEnabled = true;
        }

        var key = DeviceKey(live.Transport, live.DeviceIndex);

        if (live.Name is null && ShouldAttempt("name:" + key, now, NameReadInterval))
        {
            live.Name = TryReadName(transport: live.Transport, deviceIndex: live.DeviceIndex)
                ?? TryReadReceiverCodename(live.Transport, live.DeviceIndex);
        }

        if (ShouldAttempt("battery:" + key, now, BatteryReadInterval))
        {
            var probe = ReadBattery(live);
            if (probe.Battery.IsAvailable)
            {
                live.Battery = probe.Battery;
                live.BatteryDetail = probe.Detail;
            }
        }
    }

    private bool ShouldAttempt(string key, DateTimeOffset now, TimeSpan interval)
    {
        var last = _attempts.GetOrAdd(key, DateTimeOffset.MinValue);
        if (now - last < interval)
        {
            return false;
        }

        _attempts[key] = now;
        return true;
    }

    /// <summary>Включает в устройстве нотификации о заряде (BATTERY_STATUS = 0x100000). True — подтверждено устройством.</summary>
    private static bool TryEnableBatteryNotifications(LiveDevice live)
    {
        if (!live.Transport.TryExchange(
                WriteRegister(live.DeviceIndex, RegisterNotifications, [0x00, 0x00, 0x10]),
                live.DeviceIndex,
                ProbeTimeout,
                out var reply))
        {
            return false;
        }

        return GetErrorCode(reply) is null;
    }

    private static GearObservation? BuildObservation(LiveDevice live)
    {
        // Безымянные устройства не показываем: «Logitech-устройство 1» пользователю ничего не говорит.
        var name = live.Name ?? KindName(live.Kind);
        if (name is null)
        {
            return null;
        }

        return new GearObservation
        {
            DeviceId = $"hidpp:{live.Transport.DevicePath}#{live.DeviceIndex}",
            Name = name,
            Source = "Logitech HID++",
            Kind = GearKindClassifier.Classify(name, live.Kind),
            IsConnected = live.Online,
            IsTrusted = true,
            HasFault = false,
            Battery = live.Battery,
            Detail = ComposeDetail(live),
        };
    }

    private static string? ComposeDetail(LiveDevice live)
    {
        var parts = new List<string>();

        if (live.BatteryDetail is not null)
        {
            parts.Add(live.BatteryDetail);
        }

        if (live.Wpid is not null)
        {
            parts.Add($"WPID {live.Wpid}");
        }

        if (!live.Online)
        {
            parts.Add("не на связи");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string? KindName(GearKind kind) => kind switch
    {
        GearKind.Mouse => "Logitech-мышь",
        GearKind.Keyboard => "Logitech-клавиатура",
        GearKind.Gamepad => "Logitech-геймпад",
        GearKind.Headset => "Logitech-наушники",
        _ => null,
    };

    private static GearKind MapDeviceKind(byte kind) => kind switch
    {
        0x01 or 0x03 => GearKind.Keyboard,   // keyboard, numpad
        0x02 or 0x08 or 0x09 => GearKind.Mouse, // mouse, trackball, touchpad
        0x0B or 0x0C => GearKind.Gamepad,    // gamepad, joystick
        0x0D => GearKind.Headset,
        _ => GearKind.Other,
    };

    /// <summary>Разбирает прочитанный буфер: в одном чтении может прийти несколько отчётов подряд.</summary>
    private void HandleIncomingBuffer(HidppTransport transport, byte[] data)
    {
        var offset = 0;

        while (offset < data.Length)
        {
            var length = data[offset] switch
            {
                0x10 => 7,
                0x11 => 20,
                _ => 0,
            };

            if (length == 0 || offset + length > data.Length)
            {
                return;
            }

            HandleIncoming(transport, data.AsSpan(offset, length).ToArray());
            offset += length;
        }
    }

    private void HandleIncoming(HidppTransport transport, byte[] data)
    {
        if (data.Length < 5 || data[0] is not (0x10 or 0x11))
        {
            return;
        }

        var deviceIndex = data[1];
        if (deviceIndex == DeviceIndexReceiver)
        {
            return;
        }

        var subId = data[2];
        var payload = data[4..];

        switch (subId)
        {
            case SubIdConnectionNotification when payload.Length >= 3:
            {
                var online = (payload[0] & 0x40) == 0;
                var kind = MapDeviceKind((byte)(payload[0] & 0x0F));
                var wpid = $"{payload[2]:X2}{payload[1]:X2}";

                var live = GetOrCreate(transport, deviceIndex);
                live.Online = online;
                live.Wpid = wpid;

                if (kind != GearKind.Other)
                {
                    live.Kind = kind;
                }

                live.LastEventUtc = DateTimeOffset.UtcNow;
                break;
            }

            case 0x07 or 0x0D:
            {
                var battery = ParseBatteryNotification(subId, payload, out var detail);
                if (battery is not null)
                {
                    var live = GetOrCreate(transport, deviceIndex);
                    live.Battery = battery;
                    live.BatteryDetail = detail;
                    live.Online = true;
                    live.LastEventUtc = DateTimeOffset.UtcNow;
                }

                break;
            }

            default:
            {
                // Нотификация HID++ 2.0: swId = 0, а featureIndex совпадает с известной фичей заряда.
                if (subId >= 0x80 || (data[3] & 0x0F) != 0)
                {
                    break;
                }

                if (!_devices.TryGetValue(DeviceKey(transport, deviceIndex), out var live))
                {
                    break;
                }

                if (live.UnifiedBatteryFeature is { } unifiedFeature && subId == unifiedFeature && payload.Length >= 4)
                {
                    var probe = ParseUnifiedBattery(payload, suffix: "нотификация");
                    live.Battery = probe.Battery;
                    live.BatteryDetail = probe.Detail;
                    live.LastEventUtc = DateTimeOffset.UtcNow;
                }
                else if (live.BatteryStatusFeature is { } statusFeature && subId == statusFeature && payload.Length >= 3)
                {
                    var probe = ParseBatteryStatus(payload, suffix: "нотификация");
                    live.Battery = probe.Battery;
                    live.BatteryDetail = probe.Detail;
                    live.LastEventUtc = DateTimeOffset.UtcNow;
                }

                break;
            }
        }
    }

    private static BatteryReading? ParseBatteryNotification(byte subId, byte[] payload, out string? detail)
    {
        detail = null;

        if (subId == 0x0D && payload.Length >= 1)
        {
            var charge = payload[0];
            if (charge is > 0 and <= 100)
            {
                var statusNibble = payload.Length > 2 ? (byte)(payload[2] & 0xF0) : (byte)0;
                var charging = statusNibble is 0x50 or 0x90;
                detail = charging ? "Заряжается (нотификация)" : "по нотификации";
                return new BatteryReading { Percent = charge, IsCharging = charging };
            }
        }
        else if (subId == 0x07 && payload.Length >= 2)
        {
            var coarse = payload[0] switch
            {
                7 => CoarseBatteryLevel.Full,
                5 => CoarseBatteryLevel.High,
                3 => CoarseBatteryLevel.Low,
                1 => CoarseBatteryLevel.Empty,
                _ => CoarseBatteryLevel.Unknown,
            };

            if (coarse != CoarseBatteryLevel.Unknown)
            {
                var charging = (payload[1] & 0x21) == 0x21 || (payload[1] & 0x22) == 0x22;
                detail = charging ? "Заряжается (нотификация)" : "по нотификации";
                return new BatteryReading { Coarse = coarse, IsCharging = charging };
            }
        }

        return null;
    }

    private BatteryProbe ReadBattery(LiveDevice live)
        => TryReadViaFeature(live) ?? ReadViaRegisters(live.Transport, live.DeviceIndex);

    /// <summary>HID++ 2.0: UNIFIED_BATTERY (0x1004) → BATTERY_STATUS (0x1001) → BATTERY_VOLTAGE (0x1000).</summary>
    private BatteryProbe? TryReadViaFeature(LiveDevice live)
    {
        if (live.UnifiedBatteryFeature is null
            && TryRootGetFeature(live.Transport, live.DeviceIndex, FeatureUnifiedBattery, out var unified)
            && unified != 0)
        {
            live.UnifiedBatteryFeature = unified;
        }

        if (live.UnifiedBatteryFeature is { } unifiedFeature)
        {
            if (!TrySendFeature(live.Transport, live.DeviceIndex, unifiedFeature, 0x00, out var reply) || reply.Length < 4)
            {
                return BatteryProbe.Faulted("Не удалось прочитать заряд");
            }

            return ParseUnifiedBattery(reply, suffix: null);
        }

        if (live.BatteryStatusFeature is null
            && TryRootGetFeature(live.Transport, live.DeviceIndex, FeatureBatteryStatus, out var status)
            && status != 0)
        {
            live.BatteryStatusFeature = status;
        }

        if (live.BatteryStatusFeature is { } statusFeature)
        {
            if (!TrySendFeature(live.Transport, live.DeviceIndex, statusFeature, 0x00, out var reply) || reply.Length < 3)
            {
                return BatteryProbe.Faulted("Не удалось прочитать заряд");
            }

            return ParseBatteryStatus(reply, suffix: null);
        }

        if (live.BatteryVoltageFeature is null
            && TryRootGetFeature(live.Transport, live.DeviceIndex, FeatureBatteryVoltage, out var voltage)
            && voltage != 0)
        {
            live.BatteryVoltageFeature = voltage;
        }

        if (live.BatteryVoltageFeature is { } voltageFeature)
        {
            if (!TrySendFeature(live.Transport, live.DeviceIndex, voltageFeature, 0x00, out var reply) || reply.Length < 2)
            {
                return BatteryProbe.Faulted("Не удалось прочитать заряд");
            }

            var millivolts = (reply[0] << 8) | reply[1];
            return new BatteryProbe(
                new BatteryReading { Percent = HidppCodec.EstimatePercentFromMillivolts(millivolts) },
                HasFault: false,
                $"≈ по напряжению ({millivolts} мВ)");
        }

        return null; // HID++ 2.0 недоступен: старое устройство или устройство спит.
    }

    private static BatteryProbe ParseUnifiedBattery(byte[] reply, string? suffix)
    {
        var percent = Math.Min(reply[0], (byte)100);
        var chargingStatus = reply[2];
        var externalPower = reply[3];
        var charging = chargingStatus is 1 or 2 || externalPower != 0;

        var detail = chargingStatus switch
        {
            1 => "Заряжается",
            2 => "Заряд завершён",
            3 => "Ошибка зарядки",
            _ => charging ? "На внешнем питании" : "Аккумулятор",
        };

        return new BatteryProbe(
            new BatteryReading
            {
                Percent = percent,
                Coarse = HidppCodec.MapLevel(reply[1]),
                IsCharging = charging,
            },
            chargingStatus == 3,
            suffix is null ? detail : $"{detail} ({suffix})");
    }

    private static BatteryProbe ParseBatteryStatus(byte[] reply, string? suffix)
    {
        var status = reply[2];
        var charging = status is 1 or 2 or 3 or 4;

        var detail = status switch
        {
            1 or 2 or 4 => "Заряжается (≈)",
            3 => "Заряд завершён (≈)",
            5 => "Проблема с батареей",
            6 => "Перегрев батареи",
            _ => "≈ по данным устройства",
        };

        return new BatteryProbe(
            new BatteryReading { Percent = Math.Min(reply[0], (byte)100), IsCharging = charging },
            status is 5 or 6,
            suffix is null ? detail : $"{detail} ({suffix})");
    }

    /// <summary>HID++ 1.0: регистр 0x0D (точный процент) и 0x07 (огрублённый уровень).</summary>
    private static BatteryProbe ReadViaRegisters(HidppTransport transport, byte deviceIndex)
    {
        if (transport.TryExchange(ReadRegister(deviceIndex, 0x0D), deviceIndex, RequestTimeout, out var chargeReply)
            && TryParseRegisterData(chargeReply, out var chargeData)
            && chargeData.Length >= 1
            && chargeData[0] is > 0 and <= 100)
        {
            var statusByte = chargeData.Length > 2 ? (byte)(chargeData[2] & 0xF0) : (byte)0;
            var charging = statusByte is 0x50 or 0x90;

            return new BatteryProbe(
                new BatteryReading { Percent = chargeData[0], IsCharging = charging },
                HasFault: false,
                charging ? "Заряжается" : "≈ по данным устройства");
        }

        if (transport.TryExchange(ReadRegister(deviceIndex, 0x07), deviceIndex, RequestTimeout, out var statusReply)
            && TryParseRegisterData(statusReply, out var statusData)
            && statusData.Length >= 2)
        {
            var coarse = statusData[0] switch
            {
                7 => CoarseBatteryLevel.Full,
                5 => CoarseBatteryLevel.High,
                3 => CoarseBatteryLevel.Low,
                1 => CoarseBatteryLevel.Empty,
                _ => CoarseBatteryLevel.Unknown,
            };

            if (coarse != CoarseBatteryLevel.Unknown)
            {
                var charging = (statusData[1] & 0x21) == 0x21 || (statusData[1] & 0x22) == 0x22;

                return new BatteryProbe(
                    new BatteryReading { Coarse = coarse, IsCharging = charging },
                    HasFault: false,
                    charging ? "Заряжается" : "≈ по данным устройства");
            }
        }

        return BatteryProbe.NotSupported;
    }

    private static string? TryReadName(HidppTransport transport, byte deviceIndex)
    {
        if (!TryRootGetFeature(transport, deviceIndex, FeatureDeviceName, out var nameFeature))
        {
            return null;
        }

        if (!TrySendFeature(transport, deviceIndex, nameFeature, 0x00, out var countReply) || countReply.Length == 0)
        {
            return null;
        }

        var chunkCount = Math.Min(countReply[0], (byte)4);
        var builder = new StringBuilder();

        for (byte chunk = 0; chunk < chunkCount && builder.Length < 48; chunk++)
        {
            if (!TrySendFeature(transport, deviceIndex, nameFeature, 0x01, out var chunkReply, chunk))
            {
                break;
            }

            foreach (var value in chunkReply)
            {
                if (value == 0 || builder.Length >= 48)
                {
                    break;
                }

                builder.Append((char)value);
            }
        }

        var name = builder.ToString().Trim('\0', ' ');
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string? TryReadReceiverCodename(HidppTransport transport, byte deviceIndex)
    {
        if (!transport.LooksLikeReceiver)
        {
            return null;
        }

        var request = RegisterRequest(DeviceIndexReceiver, RegisterInfoRequest, (byte)(0x40 + deviceIndex - 1));

        if (!transport.TryExchange(request, DeviceIndexReceiver, RequestTimeout, out var reply) || GetErrorCode(reply) is not null)
        {
            return null;
        }

        var data = reply.Length > 4 ? reply[4..] : [];
        if (data.Length < 2)
        {
            return null;
        }

        var length = Math.Min(data[1], data.Length - 2);
        var text = Encoding.ASCII.GetString(data, 2, length).Trim('\0', ' ');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool TryRootGetFeature(HidppTransport transport, byte deviceIndex, ushort featureId, out byte featureIndex)
    {
        featureIndex = 0;

        if (!transport.TrySend(
                deviceIndex,
                RootFeatureIndex,
                functionId: 0x00,
                SoftwareId,
                ProbeTimeout,
                out var response,
                parameter0: (byte)(featureId >> 8),
                parameter1: (byte)(featureId & 0xFF)))
        {
            return false;
        }

        if (response.IsError || response.Parameters.Length == 0)
        {
            return false;
        }

        featureIndex = response.Parameters[0];
        return featureIndex != 0;
    }

    private static bool TrySendFeature(
        HidppTransport transport,
        byte deviceIndex,
        byte featureIndex,
        byte functionId,
        out byte[] parameters,
        byte parameter0 = 0,
        byte parameter1 = 0,
        byte parameter2 = 0)
    {
        parameters = [];

        if (!transport.TrySend(deviceIndex, featureIndex, functionId, SoftwareId, RequestTimeout, out var response, parameter0, parameter1, parameter2)
            || response.IsError)
        {
            return false;
        }

        parameters = response.Parameters;
        return true;
    }

    private static byte[] RegisterRequest(byte deviceIndex, ushort requestId, byte parameter0 = 0, byte parameter1 = 0, byte parameter2 = 0)
        => [0x10, deviceIndex, (byte)(requestId >> 8), (byte)(requestId & 0xFF), parameter0, parameter1, parameter2];

    private static byte[] ReadRegister(byte deviceIndex, ushort register)
        => RegisterRequest(deviceIndex, (ushort)(0x8100 | (register & 0x2FF)));

    private static byte[] WriteRegister(byte deviceIndex, ushort register, byte[] parameters)
    {
        var request = RegisterRequest(deviceIndex, (ushort)(0x8000 | (register & 0x2FF)));
        Array.Copy(parameters, 0, request, 4, Math.Min(parameters.Length, 3));
        return request;
    }

    private static byte? GetErrorCode(byte[] reply)
        => reply.Length > 5 && reply[2] is 0x8F or 0xFF ? reply[5] : null;

    private static bool TryParseRegisterData(byte[] reply, out byte[] data)
    {
        data = [];

        if (reply.Length < 5 || GetErrorCode(reply) is not null)
        {
            return false;
        }

        data = reply[4..];
        return data.Length > 0;
    }

    private void EnsureTransports()
    {
        lock (_transports)
        {
            if (_transports.Count > 0)
            {
                return;
            }

            _transports.AddRange(HidppTransport.FindAll());

            // Любое прочитанное сообщение — потенциальная нотификация: разбираем всё, что приходит.
            foreach (var transport in _transports)
            {
                var captured = transport;
                captured.MessageRead += data => HandleIncomingBuffer(captured, data);
            }
        }
    }

    /// <summary>Слушает входные отчёты в течение заданного окна и разбирает нотификации.</summary>
    private void CollectNotifications(TimeSpan window)
    {
        var buffer = new byte[64];
        var deadline = DateTimeOffset.UtcNow + window;

        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var transport in _transports)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    break;
                }

                try
                {
                    if (transport.TryReadRaw(buffer, NotificationReadTimeout, out var data))
                    {
                        HandleIncomingBuffer(transport, data);
                    }
                }
                catch
                {
                }
            }
        }
    }

    private LiveDevice GetOrCreate(HidppTransport transport, byte deviceIndex)
        => _devices.GetOrAdd(
            DeviceKey(transport, deviceIndex),
            _ => new LiveDevice { Transport = transport, DeviceIndex = deviceIndex });

    private static string DeviceKey(HidppTransport transport, byte deviceIndex)
        => $"{transport.DevicePath}#{deviceIndex}";

    private sealed class LiveDevice
    {
        public required HidppTransport Transport { get; init; }

        public required byte DeviceIndex { get; init; }

        public GearKind Kind { get; set; } = GearKind.Other;

        public string? Wpid { get; set; }

        public string? Name { get; set; }

        public bool Online { get; set; } = true;

        public bool NotificationsEnabled { get; set; }

        public byte? UnifiedBatteryFeature { get; set; }

        public byte? BatteryStatusFeature { get; set; }

        public byte? BatteryVoltageFeature { get; set; }

        public BatteryReading Battery { get; set; } = BatteryReading.Unknown;

        public string? BatteryDetail { get; set; }

        public DateTimeOffset LastEventUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private readonly record struct BatteryProbe(BatteryReading Battery, bool HasFault, string? Detail)
    {
        public static BatteryProbe NotSupported { get; } = new(BatteryReading.Unknown, HasFault: false, Detail: null);

        public static BatteryProbe Faulted(string detail) => new(BatteryReading.Unknown, HasFault: true, detail);
    }
}
