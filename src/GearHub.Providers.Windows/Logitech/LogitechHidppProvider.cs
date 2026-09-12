using System.Collections.Concurrent;
using System.Text;
using GearHub.Core.Abstractions;
using GearHub.Core.Models;
using GearHub.Core.Services;

namespace GearHub.Providers.Windows.Logitech;

/// <summary>
/// Logitech через HID++.
/// Нотификации приёмника (sub_id 0x41) дают список спаренных устройств и их WPID.
/// Заряд читается HID++ 2.0-запросами через длинный канал приёмника (0xFF00/0x0002, кадры 0x11):
/// UNIFIED_BATTERY 0x1004 → BATTERY_STATUS 0x1000 → BATTERY_VOLTAGE 0x1001; перед опросом — ping.
/// У старых устройств есть запасной путь через регистры HID++ 1.0 (0x0D/0x07).
/// </summary>
public sealed class LogitechHidppProvider : IGearProvider, IDisposable
{
    /// <summary>SoftwareId как в Solaar: старший бит установлен, чтобы отличать ответы от нотификаций (swId = 0).</summary>
    private const byte SoftwareId = 0x0B;

    private const byte DeviceIndexReceiver = 0xFF;
    private const byte RootFeatureIndex = 0x00;
    private const byte SubIdConnectionNotification = 0x41;

    /// <summary>Псевдо-индекс для устройства Lightspeed-донгла (G435): у него нет слотов приёмника.</summary>
    private const byte HeadsetDeviceIndex = 0x01;

    /// <summary>Сколько без кадров от донгла считаем наушники отключёнными.</summary>
    private static readonly TimeSpan HeadsetSilenceTimeout = TimeSpan.FromSeconds(20);

    private const ushort RegisterReceiverConnection = 0x02;
    private const ushort RegisterNotifications = 0x00;
    private const ushort RegisterInfoRequest = 0x83B5;

    private const ushort FeatureDeviceName = 0x0005;
    private const ushort FeatureBatteryStatus = 0x1000;   // [уровень%, следующий%, статус]
    private const ushort FeatureBatteryVoltage = 0x1001;  // [напряжение BE, флаги]
    private const ushort FeatureUnifiedBattery = 0x1004;  // [дискрет%, огрублённый уровень, статус]

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BatteryReadInterval = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan NotificationWindow = TimeSpan.FromMilliseconds(4000);
    private static readonly TimeSpan NotificationReadTimeout = TimeSpan.FromMilliseconds(150);

    private readonly List<HidppTransport> _transports = [];
    private readonly ConcurrentDictionary<string, LiveDevice> _devices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _attempts = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();

    public string ProviderName => "Logitech HID++";

    public Task<IReadOnlyList<GearObservation>> DiscoverAsync(CancellationToken cancellationToken)
        => Task.Run(() => (IReadOnlyList<GearObservation>)Scan(cancellationToken), cancellationToken);

    public void Dispose()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch
        {
        }

        lock (_transports)
        {
            foreach (var transport in _transports)
            {
                transport.Dispose();
            }

            _transports.Clear();
        }

        _lifetime.Dispose();
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
                else if (transport.MaxInputReportLength < 64)
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

        // Нотификацию легко пропустить (устройство сменило канал и т.п.): проверяем слоты ping-ом.
        foreach (var transport in _transports)
        {
            if (!transport.LooksLikeReceiver || !transport.HasDeviceChannel)
            {
                continue;
            }

            for (byte index = 1; index <= 6; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var key = DeviceKey(transport, index);
                if (_devices.ContainsKey(key))
                {
                    continue;
                }

                if (Ping(transport, index, PingTimeout))
                {
                    _devices.GetOrAdd(key, _ => new LiveDevice { Transport = transport, DeviceIndex = index });
                }
            }
        }

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

        var probe = ReadBattery(live);
        if (probe.Battery.IsAvailable)
        {
            live.Battery = probe.Battery;
            live.BatteryDetail = probe.Detail;
        }

        if (probe.Answered || probe.Battery.IsAvailable)
        {
            return;
        }

        _devices.TryRemove(key, out _);
    }

    private void RefreshLiveDevice(LiveDevice live, DateTimeOffset now)
    {
        if (!live.NotificationsEnabled && TryEnableBatteryNotifications(live))
        {
            live.NotificationsEnabled = true;
        }

        var key = DeviceKey(live.Transport, live.DeviceIndex);

        if (live.Name is null && ShouldAttempt("name:" + key, now, TimeSpan.FromMinutes(2)))
        {
            live.Name = ReadDeviceName(live)
                ?? TryReadName(transport: live.Transport, deviceIndex: live.DeviceIndex)
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
        else if (live.LastProbeAnswered == false)
        {
            parts.Add("устройство спит");
        }
        else if (live.LastProbeAnswered == true && !live.Battery.IsAvailable)
        {
            parts.Add("заряд не читается");
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

    /// <summary>
    /// Фоновый слушатель Lightspeed-донгла (G435 и подобные): донгл сам периодически
    /// (~6.4 c) присылает 65-байтный статус-кадр с зарядом наушников.
    /// </summary>
    private void StartHeadsetListener(HidppTransport transport)
    {
        var thread = new Thread(() => ListenHeadset(transport))
        {
            IsBackground = true,
            Name = "GearHub-LogitechHeadset",
        };
        thread.Start();
    }

    private void ListenHeadset(HidppTransport transport)
    {
        var buffer = new byte[128];
        var key = DeviceKey(transport, HeadsetDeviceIndex);

        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                if (_devices.TryGetValue(key, out var idle)
                    && idle.LastEventUtc < DateTimeOffset.UtcNow - HeadsetSilenceTimeout)
                {
                    idle.Online = false;
                }

                if (!transport.TryReadRaw(buffer, TimeSpan.FromMilliseconds(250), out var data))
                {
                    continue;
                }

                if (!TryParseLightspeedHeadset(data, out var battery, out var detail))
                {
                    continue;
                }

                var live = _devices.GetOrAdd(key, _ => new LiveDevice
                {
                    Transport = transport,
                    DeviceIndex = HeadsetDeviceIndex,
                    Name = transport.ProductName,
                    Kind = GearKind.Headset,
                });

                live.Kind = GearKind.Headset;
                live.Online = true;
                live.Battery = battery;
                live.BatteryDetail = detail;
                live.LastEventUtc = DateTimeOffset.UtcNow;
            }
            catch
            {
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>
    /// Кадр Lightspeed-донгла G435 (65 байт): 00 1B 50 49 01 C0 [таймстамп 4Б] 03 00 11 [счётчик] 1C
    /// 50 49 11 [счётчик] [таймстамп 4Б] 01 04 00 04 01 [таймер] [заряд %] ... — заряд в байте 28,
    /// байт 27 медленно уменьшается (оценочное время работы). Точная калибровка уточняется по G HUB.
    /// </summary>
    private static bool TryParseLightspeedHeadset(byte[] data, out BatteryReading battery, out string? detail)
    {
        battery = BatteryReading.Unknown;
        detail = null;

        if (data.Length < 30
            || data[0] != 0x00
            || data[1] != 0x1B
            || data[2] != 0x50
            || data[3] != 0x49
            || data[4] != 0x01)
        {
            return false;
        }

        var percent = data[28];
        if (percent is 0 or > 100)
        {
            return false;
        }

        battery = new BatteryReading { Percent = percent };
        detail = "≈ по данным донгла Lightspeed";
        return true;
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

                if (live.UnifiedBatteryFeature is { } unifiedFeature && subId == unifiedFeature && payload.Length >= 3)
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
    {
        var probe = TryReadViaDeviceChannel(live);
        if (probe is { } found)
        {
            live.LastProbeAnswered = found.Answered;
            return found;
        }

        // Старые устройства и моноприёмники: короткий канал и регистры HID++ 1.0.
        var legacy = ReadViaRegisters(live.Transport, live.DeviceIndex);
        live.LastProbeAnswered = legacy.Answered;
        return legacy;
    }

    /// <summary>
    /// HID++ 2.0 через длинный канал (0x11, usage 0xFF00/0x0002). Сначала ping:
    /// если устройство спит, запросы к нему бессмысленны.
    /// </summary>
    private BatteryProbe? TryReadViaDeviceChannel(LiveDevice live)
    {
        var transport = live.Transport;
        if (!transport.HasDeviceChannel)
        {
            return null;
        }

        if (!Ping(transport, live.DeviceIndex, ProbeTimeout))
        {
            return BatteryProbe.NotSupported; // устройство не ответило: скорее всего, спит
        }

        // UNIFIED_BATTERY (0x1004): функция 1 отдаёт [дискрет%, уровень, статус].
        if (live.UnifiedBatteryFeature is null
            && TryRootGetFeatureLong(transport, live.DeviceIndex, FeatureUnifiedBattery, out var unified)
            && unified != 0)
        {
            live.UnifiedBatteryFeature = unified;
        }

        if (live.UnifiedBatteryFeature is { } unifiedFeature)
        {
            if (transport.TryDeviceCall(live.DeviceIndex, unifiedFeature, 0x01, RequestTimeout, out var reply)
                && !reply.IsError
                && reply.Payload.Length >= 3)
            {
                return ParseUnifiedBattery(reply.Payload, suffix: null);
            }

            return BatteryProbe.Faulted("заряд не читается");
        }

        // BATTERY_STATUS (0x1000): функция 0 отдаёт [уровень%, следующий%, статус].
        if (live.BatteryStatusFeature is null
            && TryRootGetFeatureLong(transport, live.DeviceIndex, FeatureBatteryStatus, out var status)
            && status != 0)
        {
            live.BatteryStatusFeature = status;
        }

        if (live.BatteryStatusFeature is { } statusFeature)
        {
            if (transport.TryDeviceCall(live.DeviceIndex, statusFeature, 0x00, RequestTimeout, out var reply)
                && !reply.IsError
                && reply.Payload.Length >= 3)
            {
                return ParseBatteryStatus(reply.Payload, suffix: null);
            }

            return BatteryProbe.Faulted("заряд не читается");
        }

        // BATTERY_VOLTAGE (0x1001): функция 0 отдаёт [напряжение BE, флаги].
        if (live.BatteryVoltageFeature is null
            && TryRootGetFeatureLong(transport, live.DeviceIndex, FeatureBatteryVoltage, out var voltage)
            && voltage != 0)
        {
            live.BatteryVoltageFeature = voltage;
        }

        if (live.BatteryVoltageFeature is { } voltageFeature)
        {
            if (transport.TryDeviceCall(live.DeviceIndex, voltageFeature, 0x00, RequestTimeout, out var reply)
                && !reply.IsError
                && reply.Payload.Length >= 2)
            {
                var millivolts = (reply.Payload[0] << 8) | reply.Payload[1];
                var charging = reply.Payload.Length > 2 && (reply.Payload[2] & 0x80) != 0;

                return new BatteryProbe(
                    new BatteryReading
                    {
                        Percent = HidppCodec.EstimatePercentFromMillivolts(millivolts),
                        IsCharging = charging,
                    },
                    HasFault: false,
                    charging ? "Заряжается" : "≈ по напряжению",
                    Answered: true);
            }

            return BatteryProbe.Faulted("заряд не читается");
        }

        return BatteryProbe.NotSupported;
    }

    /// <summary>Ping (HID++ 2.0 ROOT, функция 1): отвечает ли устройство прямо сейчас.</summary>
    private static bool Ping(HidppTransport transport, byte deviceIndex, TimeSpan timeout)
        => transport.TryDeviceCall(deviceIndex, RootFeatureIndex, 0x01, timeout, out var reply, 0x00, 0x00, 0xA5)
            && !reply.IsError
            && reply.Payload.Length >= 2;

    private static bool TryRootGetFeatureLong(HidppTransport transport, byte deviceIndex, ushort featureId, out byte featureIndex)
    {
        featureIndex = 0;

        if (!transport.TryDeviceCall(
                deviceIndex,
                RootFeatureIndex,
                0x00,
                RequestTimeout,
                out var reply,
                (byte)(featureId >> 8),
                (byte)(featureId & 0xFF),
                0x00))
        {
            return false;
        }

        if (reply.IsError || reply.Payload.Length == 0)
        {
            return false;
        }

        featureIndex = reply.Payload[0];
        return featureIndex != 0;
    }

    /// <summary>Описание статусного байта батареи: зарядка, неисправность, текст.</summary>
    private static (bool Charging, bool Fault, string? Detail) DescribeBatteryStatus(byte status) => status switch
    {
        1 => (true, false, "Заряжается"),
        2 => (false, false, "Заряд завершён"),
        3 => (true, false, "Медленная зарядка"),
        4 => (false, true, "Проблема с батареей"),
        5 => (false, true, "Перегрев батареи"),
        6 => (false, true, "Ошибка зарядки"),
        _ => (false, false, null),
    };

    private static BatteryProbe ParseUnifiedBattery(byte[] reply, string? suffix)
    {
        var discharge = reply[0];
        int? percent = discharge switch
        {
            0 => null,
            <= 100 => discharge,
            _ => (int)Math.Round(discharge * 100.0 / 255.0),
        };

        var coarse = reply[1] switch
        {
            8 => CoarseBatteryLevel.Full,
            4 => CoarseBatteryLevel.High,
            2 => CoarseBatteryLevel.Low,
            1 => CoarseBatteryLevel.Empty,
            _ => CoarseBatteryLevel.Unknown,
        };

        var (charging, fault, detail) = DescribeBatteryStatus(reply[2]);

        return new BatteryProbe(
            new BatteryReading
            {
                Percent = percent,
                Coarse = coarse,
                IsCharging = charging,
            },
            fault,
            CombineDetail(detail, suffix),
            Answered: true);
    }

    private static BatteryProbe ParseBatteryStatus(byte[] reply, string? suffix)
    {
        var level = reply[0];
        int? percent = level switch
        {
            0 => null,
            <= 100 => level,
            _ => (int)Math.Round(level * 100.0 / 255.0),
        };

        var status = reply.Length > 2 ? reply[2] : (byte)0;
        var (charging, fault, detail) = DescribeBatteryStatus(status);

        return new BatteryProbe(
            new BatteryReading { Percent = percent, IsCharging = charging },
            fault,
            CombineDetail(detail, suffix),
            Answered: true);
    }

    private static string? CombineDetail(string? detail, string? suffix)
        => detail is null ? null : suffix is null ? detail : $"{detail} ({suffix})";

    /// <summary>HID++ 1.0: регистр 0x0D (точный процент) и 0x07 (огрублённый уровень).</summary>
    private static BatteryProbe ReadViaRegisters(HidppTransport transport, byte deviceIndex)
    {
        var answered = false;

        if (transport.TryExchange(ReadRegister(deviceIndex, 0x0D), deviceIndex, RequestTimeout, out var chargeReply))
        {
            answered = true;

            if (TryParseRegisterData(chargeReply, out var chargeData)
                && chargeData.Length >= 1
                && chargeData[0] is > 0 and <= 100)
            {
                var statusByte = chargeData.Length > 2 ? (byte)(chargeData[2] & 0xF0) : (byte)0;
                var charging = statusByte is 0x50 or 0x90;

                return new BatteryProbe(
                    new BatteryReading { Percent = chargeData[0], IsCharging = charging },
                    HasFault: false,
                    charging ? "Заряжается" : "≈ по данным устройства",
                    Answered: true);
            }
        }

        if (transport.TryExchange(ReadRegister(deviceIndex, 0x07), deviceIndex, RequestTimeout, out var statusReply))
        {
            answered = true;

            if (TryParseRegisterData(statusReply, out var statusData) && statusData.Length >= 2)
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
                        charging ? "Заряжается" : "≈ по данным устройства",
                        Answered: true);
                }
            }
        }

        return new BatteryProbe(BatteryReading.Unknown, HasFault: false, Detail: null, Answered: answered);
    }

    /// <summary>
    /// Имя устройства через HID++ 2.0 (DEVICE_NAME 0x0005): функция 2 — вид устройства,
    /// функция 0 — длина имени, функция 1 — куски UTF-8.
    /// </summary>
    private static string? ReadDeviceName(LiveDevice live)
    {
        var transport = live.Transport;
        if (!transport.HasDeviceChannel)
        {
            return null;
        }

        if (live.DeviceNameFeature is null
            && TryRootGetFeatureLong(transport, live.DeviceIndex, FeatureDeviceName, out var nameFeature)
            && nameFeature != 0)
        {
            live.DeviceNameFeature = nameFeature;
        }

        if (live.DeviceNameFeature is not { } feature)
        {
            return null;
        }

        if (live.Kind == GearKind.Other
            && transport.TryDeviceCall(live.DeviceIndex, feature, 0x02, RequestTimeout, out var kindReply)
            && !kindReply.IsError
            && kindReply.Payload.Length >= 1)
        {
            var kind = MapDeviceKind(kindReply.Payload[0]);
            if (kind != GearKind.Other)
            {
                live.Kind = kind;
            }
        }

        if (!transport.TryDeviceCall(live.DeviceIndex, feature, 0x00, RequestTimeout, out var countReply)
            || countReply.IsError
            || countReply.Payload.Length == 0)
        {
            return null;
        }

        var total = countReply.Payload[0];
        if (total is 0 or > 64)
        {
            return null;
        }

        var builder = new StringBuilder(total);

        while (builder.Length < total)
        {
            if (!transport.TryDeviceCall(live.DeviceIndex, feature, 0x01, RequestTimeout, out var chunk, (byte)builder.Length)
                || chunk.IsError
                || chunk.Payload.Length == 0)
            {
                break;
            }

            var count = Math.Min(chunk.Payload.Length, total - builder.Length);
            var stop = Array.IndexOf(chunk.Payload, (byte)0, 0, count);
            if (stop >= 0)
            {
                count = stop;
            }

            builder.Append(Encoding.UTF8.GetString(chunk.Payload, 0, count));
        }

        var name = builder.ToString().Trim('\0', ' ');
        return string.IsNullOrWhiteSpace(name) ? null : name;
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

                // Донгл наушников Lightspeed: статус приходит сам, нужен фоновый слушатель.
                if (!captured.LooksLikeReceiver && captured.MaxInputReportLength >= 64)
                {
                    StartHeadsetListener(captured);
                }
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

                    if (transport.TryReadRawLong(buffer, TimeSpan.FromMilliseconds(5), out var longData))
                    {
                        HandleIncomingBuffer(transport, longData);
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

        public bool? LastProbeAnswered { get; set; }

        public byte? UnifiedBatteryFeature { get; set; }

        public byte? BatteryStatusFeature { get; set; }

        public byte? BatteryVoltageFeature { get; set; }

        public byte? DeviceNameFeature { get; set; }

        public BatteryReading Battery { get; set; } = BatteryReading.Unknown;

        public string? BatteryDetail { get; set; }

        public DateTimeOffset LastEventUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private readonly record struct BatteryProbe(BatteryReading Battery, bool HasFault, string? Detail, bool Answered = false)
    {
        public static BatteryProbe NotSupported { get; } = new(BatteryReading.Unknown, HasFault: false, Detail: null, Answered: false);

        public static BatteryProbe Faulted(string detail) => new(BatteryReading.Unknown, HasFault: true, detail, Answered: true);
    }
}
