using System.Collections.Concurrent;
using System.Text;
using GearHub.Core.Abstractions;
using GearHub.Core.Localization;
using GearHub.Core.Models;
using GearHub.Core.Services;

namespace GearHub.Providers.Windows.Logitech;

/// <summary>
/// Logitech over HID++.
/// Receiver notifications (sub_id 0x41) provide the list of paired devices and their WPID.
/// Charge is read via HID++ 2.0 requests through the receiver's long channel (0xFF00/0x0002, frames 0x11):
/// UNIFIED_BATTERY 0x1004 → BATTERY_STATUS 0x1000 → BATTERY_VOLTAGE 0x1001; ping before polling.
/// Older devices have a fallback path through HID++ 1.0 registers (0x0D/0x07).
/// </summary>
public sealed class LogitechHidppProvider : IGearProvider, IDisposable
{
    /// <summary>SoftwareId as in Solaar: high bit set to distinguish responses from notifications (swId = 0).</summary>
    private const byte SoftwareId = 0x0B;

    private const byte DeviceIndexReceiver = 0xFF;
    private const byte RootFeatureIndex = 0x00;
    private const byte SubIdConnectionNotification = 0x41;

    /// <summary>Pseudo-index for the Lightspeed dongle device (G435): it has no receiver slots.</summary>
    private const byte HeadsetDeviceIndex = 0x01;

    /// <summary>How long without dongle frames before the headset is considered disconnected.</summary>
    private static readonly TimeSpan HeadsetSilenceTimeout = TimeSpan.FromSeconds(20);

    private const ushort RegisterReceiverConnection = 0x02;
    private const ushort RegisterNotifications = 0x00;
    private const ushort RegisterInfoRequest = 0x83B5;

    private const ushort FeatureDeviceName = 0x0005;
    private const ushort FeatureBatteryStatus = 0x1000;   // [level %, next %, status]
    private const ushort FeatureBatteryVoltage = 0x1001;  // [voltage BE, flags]
    private const ushort FeatureUnifiedBattery = 0x1004;  // [discrete %, coarse level, status]

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
                    // Ask the receiver to broadcast up-to-date connection information for its devices.
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

        // After the nudge, receivers send connection and charge notifications — collect them.
        CollectNotifications(NotificationWindow);

        // A notification is easy to miss (the device switched channels, etc.): probe the slots with a ping.
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
                // A single device must not break the whole scan.
            }
        }

        return results;
    }

    /// <summary>A device connected directly (Bluetooth/cable): there are no receiver notifications for it.</summary>
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

    /// <summary>Enables charge notifications on the device (BATTERY_STATUS = 0x100000). True — confirmed by the device.</summary>
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
        // Do not show unnamed devices: "Logitech device 1" tells the user nothing.
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
            parts.Add(Loc.Get("DeviceAsleep"));
        }
        else if (live.LastProbeAnswered == true && !live.Battery.IsAvailable)
        {
            parts.Add(Loc.Get("ChargeUnreadable"));
        }

        if (live.Wpid is not null)
        {
            parts.Add($"WPID {live.Wpid}");
        }

        if (!live.Online)
        {
            parts.Add(Loc.Get("NotConnected"));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// Background listener for the Lightspeed dongle (G435 and similar): the dongle itself periodically
    /// (~6.4 s) sends a 65-byte status frame with the headset charge.
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

                if (!TryParseLightspeedHeadset(data, out var hasBattery, out var battery, out var detail))
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
                live.LastEventUtc = DateTimeOffset.UtcNow;

                if (hasBattery)
                {
                    live.Battery = battery;
                    live.BatteryDetail = detail;
                }
            }
            catch
            {
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>
    /// Lightspeed dongle frame for the G435 (65 bytes, starts with 00, contains the marker 50 49).
    /// The charge is in the record &lt;status&gt; 04 00 04 01 &lt;low&gt; &lt;high&gt;, where status is the state:
    /// 01 — not charging, 03 — charging (bit 0x02). Percentages are in 1/256 format (0x32E9 = 50.91%).
    /// Frames without this record are just link status.
    /// </summary>
    private static bool TryParseLightspeedHeadset(byte[] data, out bool hasBattery, out BatteryReading battery, out string? detail)
    {
        hasBattery = false;
        battery = BatteryReading.Unknown;
        detail = null;

        if (data.Length < 30 || data[0] != 0x00 || data[2] != 0x50 || data[3] != 0x49)
        {
            return false;
        }

        for (var index = 5; index + 5 < data.Length; index++)
        {
            // The record anchor is 04 00 04 01; the byte before it is the status (bit 0x02 — charging).
            if (data[index] != 0x04
                || data[index + 1] != 0x00
                || data[index + 2] != 0x04
                || data[index + 3] != 0x01)
            {
                continue;
            }

            var status = data[index - 1];
            var raw = (data[index + 5] << 8) | data[index + 4];
            var percent = (int)Math.Round(raw / 256.0);

            if (percent is <= 0 or > 100)
            {
                continue;
            }

            hasBattery = true;
            battery = new BatteryReading
            {
                Percent = percent,
                IsCharging = (status & 0x02) != 0,
            };
            detail = Loc.Get("ApproxByDongle");
            return true;
        }

        // Dongle frame without charge: mark the device as online, leave the battery untouched.
        return true;
    }

    private static string? KindName(GearKind kind) => kind switch
    {
        GearKind.Mouse => Loc.Get("LogitechMouse"),
        GearKind.Keyboard => Loc.Get("LogitechKeyboard"),
        GearKind.Gamepad => Loc.Get("LogitechGamepad"),
        GearKind.Headset => Loc.Get("LogitechHeadset"),
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

    /// <summary>Parses the read buffer: a single read may contain several reports in a row.</summary>
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
                // HID++ 2.0 notification: swId = 0 and featureIndex matches a known charge feature.
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
                    var probe = ParseUnifiedBattery(payload, suffix: Loc.Get("ViaNotification"));
                    live.Battery = probe.Battery;
                    live.BatteryDetail = probe.Detail;
                    live.LastEventUtc = DateTimeOffset.UtcNow;
                }
                else if (live.BatteryStatusFeature is { } statusFeature && subId == statusFeature && payload.Length >= 3)
                {
                    var probe = ParseBatteryStatus(payload, suffix: Loc.Get("ViaNotification"));
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
                detail = charging ? Loc.Get("ChargingNotification") : Loc.Get("ByNotification");
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
                detail = charging ? Loc.Get("ChargingNotification") : Loc.Get("ByNotification");
                return new BatteryReading { Coarse = coarse, IsCharging = charging };
            }
        }

        return null;
    }

    private BatteryProbe ReadBattery(LiveDevice live)
    {
        if (!live.Transport.LooksLikeReceiver && live.Transport.MaxInputReportLength >= 64)
        {
            // Lightspeed headset dongle: the charge arrives on its own; it does not support active requests.
            return new BatteryProbe(live.Battery, HasFault: false, Detail: null, Answered: true);
        }

        var probe = TryReadViaDeviceChannel(live);
        if (probe is { } found)
        {
            live.LastProbeAnswered = found.Answered;
            return found;
        }

        // Older devices and single-device receivers: the short channel and HID++ 1.0 registers.
        var legacy = ReadViaRegisters(live.Transport, live.DeviceIndex);
        live.LastProbeAnswered = legacy.Answered;
        return legacy;
    }

    /// <summary>
    /// HID++ 2.0 through the long channel (0x11, usage 0xFF00/0x0002). Ping first:
    /// if the device is asleep, requests to it are pointless.
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
            return BatteryProbe.NotSupported; // device did not respond: most likely asleep
        }

        // UNIFIED_BATTERY (0x1004): function 1 returns [discrete %, level, status].
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

            return BatteryProbe.Faulted(Loc.Get("ChargeUnreadable"));
        }

        // BATTERY_STATUS (0x1000): function 0 returns [level %, next %, status].
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

            return BatteryProbe.Faulted(Loc.Get("ChargeUnreadable"));
        }

        // BATTERY_VOLTAGE (0x1001): function 0 returns [voltage BE, flags].
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
                    charging ? Loc.Get("Charging") : Loc.Get("ApproxByVoltage"),
                    Answered: true);
            }

            return BatteryProbe.Faulted(Loc.Get("ChargeUnreadable"));
        }

        return BatteryProbe.NotSupported;
    }

    /// <summary>Ping (HID++ 2.0 ROOT, function 1): whether the device responds right now.</summary>
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

    /// <summary>Description of the battery status byte: charging, fault, text.</summary>
    private static (bool Charging, bool Fault, string? Detail) DescribeBatteryStatus(byte status) => status switch
    {
        1 => (true, false, Loc.Get("Charging")),
        2 => (false, false, Loc.Get("ChargeComplete")),
        3 => (true, false, Loc.Get("SlowCharging")),
        4 => (false, true, Loc.Get("BatteryProblem")),
        5 => (false, true, Loc.Get("BatteryOverheat")),
        6 => (false, true, Loc.Get("ChargingError")),
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

    /// <summary>HID++ 1.0: register 0x0D (accurate percentage) and 0x07 (coarse level).</summary>
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
                    charging ? Loc.Get("Charging") : Loc.Get("ApproxByDevice"),
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
                        charging ? Loc.Get("Charging") : Loc.Get("ApproxByDevice"),
                        Answered: true);
                }
            }
        }

        return new BatteryProbe(BatteryReading.Unknown, HasFault: false, Detail: null, Answered: answered);
    }

    /// <summary>
    /// Device name via HID++ 2.0 (DEVICE_NAME 0x0005): function 2 — device kind,
    /// function 0 — name length, function 1 — UTF-8 chunks.
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

            // Any message read is a potential notification: parse everything that arrives.
            foreach (var transport in _transports)
            {
                var captured = transport;
                captured.MessageRead += data => HandleIncomingBuffer(captured, data);

                // Lightspeed headset dongle: status arrives on its own; a background listener is needed.
                if (!captured.LooksLikeReceiver && captured.MaxInputReportLength >= 64)
                {
                    StartHeadsetListener(captured);
                }
            }
        }
    }

    /// <summary>Listens to input reports for the given window and parses notifications.</summary>
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
