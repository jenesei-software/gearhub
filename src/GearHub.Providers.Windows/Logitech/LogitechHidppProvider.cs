using System.Text;
using GearHub.Core.Abstractions;
using GearHub.Core.Models;
using GearHub.Core.Services;

namespace GearHub.Providers.Windows.Logitech;

/// <summary>
/// Заряд Logitech-устройств через HID++.
/// Устройства за приёмниками Unifying/Bolt находятся через регистры приёмника (HID++ 1.0),
/// заряд читается через HID++ 2.0 (0x1004 / 0x1001 / 0x1000), с фолбэком на регистры 0x0D / 0x07.
/// Спящее устройство приёмник помечает ошибкой RESOURCE_ERROR — такие устройства остаются в списке
/// с последним известным зарядом.
/// </summary>
public sealed class LogitechHidppProvider : IGearProvider, IDisposable
{
    /// <summary>SoftwareId как в Solaar: старший бит установлен, чтобы отличать ответы от нотификаций.</summary>
    private const byte SoftwareId = 0x0B;

    private const byte DeviceIndexReceiver = 0xFF;
    private const byte RootFeatureIndex = 0x00;

    private const ushort RegisterReceiverConnection = 0x02;
    private const ushort RegisterInfoRequest = 0x83B5; // read receiver info (0x8100 | 0x02B5)

    private const byte ErrorConnectionRequestFailed = 0x04;
    private const byte ErrorUnknownDevice = 0x08;
    private const byte ErrorResource = 0x09;

    private const ushort FeatureBatteryVoltage = 0x1000;
    private const ushort FeatureBatteryStatus = 0x1001;
    private const ushort FeatureUnifiedBattery = 0x1004;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan TransportRefreshInterval = TimeSpan.FromSeconds(60);

    private readonly List<HidppTransport> _transports = [];
    private DateTimeOffset _lastTransportRefresh = DateTimeOffset.MinValue;

    public string ProviderName => "Logitech HID++";

    public Task<IReadOnlyList<GearObservation>> DiscoverAsync(CancellationToken cancellationToken)
        => Task.Run(() => (IReadOnlyList<GearObservation>)Scan(cancellationToken), cancellationToken);

    public void Dispose()
    {
        foreach (var transport in _transports)
        {
            transport.Dispose();
        }

        _transports.Clear();
    }

    private List<GearObservation> Scan(CancellationToken cancellationToken)
    {
        var observations = new List<GearObservation>();
        RefreshTransports();

        foreach (var transport in _transports)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (transport.LooksLikeReceiver)
                {
                    ScanReceiver(transport, observations);
                }
                else
                {
                    ScanDirectDevice(transport, observations);
                }
            }
            catch
            {
                // Одно устройство не должно ломать весь скан.
            }
        }

        return observations;
    }

    private static void ScanReceiver(HidppTransport transport, List<GearObservation> results)
    {
        transport.DrainInput();

        // «Дёрнуть» уведомления о связи — приём, который использует Solaar при старте.
        _ = transport.TryExchange(
            WriteRegister(DeviceIndexReceiver, RegisterReceiverConnection, [0x02]),
            DeviceIndexReceiver,
            ProbeTimeout,
            out _);

        for (byte slot = 1; slot <= 6; slot++)
        {
            try
            {
                var observation = InspectSlot(transport, slot);
                if (observation is not null)
                {
                    results.Add(observation);
                }
            }
            catch
            {
            }
        }
    }

    private static void ScanDirectDevice(HidppTransport transport, List<GearObservation> results)
    {
        var probe = TryReadViaFeature(transport, HidppCodec.DeviceIndexDirect);
        if (probe is null)
        {
            return;
        }

        var value = probe.Value;
        var name = transport.ProductName;

        results.Add(new GearObservation
        {
            DeviceId = $"hidpp:{transport.DevicePath}",
            Name = name,
            Source = "Logitech HID++",
            Kind = GearKindClassifier.Classify(name),
            IsConnected = true,
            IsTrusted = true,
            HasFault = value.HasFault,
            Battery = value.Battery,
            Detail = value.Detail,
        });
    }

    private static GearObservation? InspectSlot(HidppTransport transport, byte slot)
    {
        byte[]? pairingData = null;
        var isBolt = false;

        if (TryReadReceiverInfo(transport, (ushort)(0x20 + slot - 1), out var unifyingPairing))
        {
            pairingData = unifyingPairing;
        }
        else if (TryReadReceiverInfo(transport, (ushort)(0x50 + slot), out var boltPairing))
        {
            pairingData = boltPairing;
            isBolt = true;
        }

        var name = TryReadDeviceName(transport, slot, isBolt);
        var alive = IsAlive(transport, slot);

        if (pairingData is null && name is null && !alive)
        {
            return null;
        }

        var probe = ReadBattery(transport, slot);

        var displayName = name
            ?? FormatFallbackName(pairingData, isBolt, slot);

        return new GearObservation
        {
            DeviceId = $"hidpp:{transport.DevicePath}#{slot}",
            Name = displayName,
            Source = "Logitech HID++",
            Kind = GearKindClassifier.Classify(displayName),
            IsConnected = true,
            IsTrusted = true,
            HasFault = probe.HasFault,
            Battery = probe.Battery,
            Detail = probe.Detail,
        };
    }

    private static string FormatFallbackName(byte[]? pairingData, bool isBolt, byte slot)
    {
        if (pairingData is not null && pairingData.Length >= 5)
        {
            var wpid = isBolt
                ? $"{pairingData[3]:X2}{pairingData[2]:X2}"
                : $"{pairingData[3]:X2}{pairingData[4]:X2}";
            return $"Logitech {wpid}";
        }

        return $"Logitech-устройство {slot}";
    }

    private static bool TryReadReceiverInfo(HidppTransport transport, ushort subRegister, out byte[] data)
    {
        data = [];

        var request = RegisterRequest(DeviceIndexReceiver, RegisterInfoRequest, (byte)(subRegister >> 8), (byte)(subRegister & 0xFF));

        if (!transport.TryExchange(request, DeviceIndexReceiver, RequestTimeout, out var reply) || GetErrorCode(reply) is not null)
        {
            return false;
        }

        data = reply.Length > 4 ? reply[4..] : [];
        return data.Length > 0;
    }

    private static string? TryReadDeviceName(HidppTransport transport, byte slot, bool isBolt)
    {
        var request = isBolt
            ? RegisterRequest(DeviceIndexReceiver, RegisterInfoRequest, (byte)(0x60 + slot), 0x01)
            : RegisterRequest(DeviceIndexReceiver, RegisterInfoRequest, (byte)(0x40 + slot - 1));

        if (!transport.TryExchange(request, DeviceIndexReceiver, RequestTimeout, out var reply) || GetErrorCode(reply) is not null)
        {
            return null;
        }

        var data = reply.Length > 4 ? reply[4..] : [];

        // Раскладка как в Solaar: Unifying — длина в data[1], имя с data[2];
        // Bolt — длина в data[2] (до 14), имя с data[3].
        string text;
        if (isBolt)
        {
            if (data.Length < 3)
            {
                return null;
            }

            var length = Math.Min(14, (int)Math.Min(data[2], data.Length - 3));
            text = Encoding.ASCII.GetString(data, 3, length);
        }
        else
        {
            if (data.Length < 2)
            {
                return null;
            }

            var length = Math.Min(data[1], data.Length - 2);
            text = Encoding.ASCII.GetString(data, 2, length);
        }

        text = text.Trim('\0', ' ');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool IsAlive(HidppTransport transport, byte slot)
    {
        if (!transport.TryExchange(ReadRegister(slot, 0x0D), slot, ProbeTimeout, out var reply))
        {
            return false;
        }

        var error = GetErrorCode(reply);
        return error is not (ErrorUnknownDevice or ErrorResource or ErrorConnectionRequestFailed);
    }

    private static BatteryProbe ReadBattery(HidppTransport transport, byte slot)
        => TryReadViaFeature(transport, slot) ?? ReadViaRegisters(transport, slot);

    /// <summary>HID++ 2.0: UNIFIED_BATTERY (0x1004) → BATTERY_STATUS (0x1001) → BATTERY_VOLTAGE (0x1000).</summary>
    private static BatteryProbe? TryReadViaFeature(HidppTransport transport, byte deviceIndex)
    {
        if (TryRootGetFeature(transport, deviceIndex, FeatureUnifiedBattery, out var unifiedBattery))
        {
            if (!TrySendFeature(transport, deviceIndex, unifiedBattery, 0x00, out var reply) || reply.Length < 4)
            {
                return BatteryProbe.Faulted("Не удалось прочитать заряд");
            }

            var percent = Math.Min(reply[0], (byte)100);
            var level = reply[1];
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

            var battery = new BatteryReading
            {
                Percent = percent,
                Coarse = HidppCodec.MapLevel(level),
                IsCharging = charging,
            };

            return new BatteryProbe(battery, chargingStatus == 3, detail);
        }

        if (TryRootGetFeature(transport, deviceIndex, FeatureBatteryStatus, out var batteryStatus))
        {
            if (!TrySendFeature(transport, deviceIndex, batteryStatus, 0x00, out var reply) || reply.Length < 3)
            {
                return BatteryProbe.Faulted("Не удалось прочитать заряд");
            }

            var dischargeLevel = Math.Min(reply[0], (byte)100);
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
                new BatteryReading { Percent = dischargeLevel, IsCharging = charging },
                status is 5 or 6,
                detail);
        }

        if (TryRootGetFeature(transport, deviceIndex, FeatureBatteryVoltage, out var batteryVoltage))
        {
            if (!TrySendFeature(transport, deviceIndex, batteryVoltage, 0x00, out var reply) || reply.Length < 2)
            {
                return BatteryProbe.Faulted("Не удалось прочитать заряд");
            }

            var millivolts = (reply[0] << 8) | reply[1];
            return new BatteryProbe(
                new BatteryReading { Percent = HidppCodec.EstimatePercentFromMillivolts(millivolts) },
                HasFault: false,
                $"≈ по напряжению ({millivolts} мВ)");
        }

        return null; // HID++ 2.0 недоступен (старое устройство или сон).
    }

    /// <summary>HID++ 1.0: регистр 0x0D (точный процент) и 0x07 (огрублённый уровень).</summary>
    private static BatteryProbe ReadViaRegisters(HidppTransport transport, byte slot)
    {
        if (transport.TryExchange(ReadRegister(slot, 0x0D), slot, RequestTimeout, out var chargeReply)
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

        if (transport.TryExchange(ReadRegister(slot, 0x07), slot, RequestTimeout, out var statusReply)
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

            var charging = (statusData[1] & 0x21) == 0x21 || (statusData[1] & 0x22) == 0x22;

            if (coarse != CoarseBatteryLevel.Unknown)
            {
                return new BatteryProbe(
                    new BatteryReading { Coarse = coarse, IsCharging = charging },
                    HasFault: false,
                    charging ? "Заряжается" : "≈ по данным устройства");
            }
        }

        return BatteryProbe.NotSupported;
    }

    /// <summary>Root.getFeature(featureId) → индекс фичи.</summary>
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

    private static bool TrySendFeature(HidppTransport transport, byte deviceIndex, byte featureIndex, byte functionId, out byte[] parameters)
    {
        parameters = [];

        if (!transport.TrySend(deviceIndex, featureIndex, functionId, SoftwareId, RequestTimeout, out var response) || response.IsError)
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

    private void RefreshTransports()
    {
        var now = DateTimeOffset.UtcNow;

        if (_transports.Count > 0 && now - _lastTransportRefresh < TransportRefreshInterval)
        {
            return;
        }

        foreach (var transport in _transports)
        {
            transport.Dispose();
        }

        _transports.Clear();
        _lastTransportRefresh = now;
        _transports.AddRange(HidppTransport.FindAll());
    }

    private readonly record struct BatteryProbe(BatteryReading Battery, bool HasFault, string? Detail)
    {
        public static BatteryProbe NotSupported { get; } = new(BatteryReading.Unknown, HasFault: false, Detail: null);

        public static BatteryProbe Faulted(string detail) => new(BatteryReading.Unknown, HasFault: true, detail);
    }
}
