using GearHub.Core.Localization;
using HidSharp;

namespace GearHub.Providers.Windows.Logitech;

/// <summary>
/// HID++ transport: the receiver's short channel (usage 0xFF00/0x0001, reports 0x10 — registers and
/// notifications) and the devices' long channel (usage 0xFF00/0x0002, reports 0x11 — HID++ 2.0 requests).
/// </summary>
public sealed class HidppTransport : IDisposable
{
    private const int LogitechVendorId = 0x046D;
    private const int HidppUsagePage = 0xFF00;
    private const int HidppUsage = 0x0001;
    private const int HidppLongUsage = 0x0002;
    private const int MinimumReadTimeoutMs = 30;

    private readonly HidDevice _device;
    private readonly HidStream _stream;
    private readonly HidStream? _longStream;
    private readonly object _gate = new();

    /// <summary>
    /// Any message read, including notifications that are not a response to a request.
    /// Through this event the provider sees what would otherwise be discarded while parsing replies.
    /// </summary>
    public event Action<byte[]>? MessageRead;

    private HidppTransport(HidDevice device, HidStream stream, HidStream? longStream, string productName)
    {
        _device = device;
        _stream = stream;
        _longStream = longStream;
        ProductName = productName;
        _stream.ReadTimeout = MinimumReadTimeoutMs;

        if (_longStream is not null)
        {
            _longStream.ReadTimeout = MinimumReadTimeoutMs;
        }
    }

    public string DevicePath => _device.DevicePath;

    public string ProductName { get; }

    /// <summary>
    /// Long channel (usage 0xFF00/0x0002): only through it do devices behind the receiver respond
    /// to HID++ 2.0 requests with long frames 0x11. Present on Unifying/Bolt receivers.
    /// </summary>
    public bool HasDeviceChannel => _longStream is not null;

    public int MaxInputReportLength => _device.GetMaxInputReportLength();

    public int MaxOutputReportLength => _device.GetMaxOutputReportLength();

    /// <summary>Unifying/Bolt receivers present themselves as "USB Receiver" and proxy up to 6 devices.</summary>
    public bool LooksLikeReceiver => ProductName.Contains("receiver", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sends a request and returns the first reply addressed to the same device.
    /// Suitable for HID++ 1.0 requests (registers), where there is no SoftwareId.
    /// </summary>
    public bool TryExchange(byte[] request, byte deviceIndex, TimeSpan timeout, out byte[] reply)
    {
        lock (_gate)
        {
            reply = [];

            try
            {
                _stream.Write(request);
            }
            catch
            {
                return false;
            }

            var buffer = new byte[64];
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                _stream.ReadTimeout = Math.Max(MinimumReadTimeoutMs, (int)Math.Min(400, (deadline - DateTime.UtcNow).TotalMilliseconds));

                try
                {
                    var read = _stream.Read(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        RaiseMessageRead(buffer, read);
                    }

                    if (read >= 2 && buffer[1] == deviceIndex)
                    {
                        reply = buffer.AsSpan(0, read).ToArray();
                        return true;
                    }
                }
                catch (TimeoutException)
                {
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// HID++ 2.0 request to a device behind the receiver: a long frame 0x11 to channel 0xFF00/0x0002.
    /// The reply is matched by device index, feature, and function byte (function &lt;&lt; 4 | 0x0B),
    /// including the 0xFF error frame. While waiting, we keep reading the short channel (notifications).
    /// </summary>
    public bool TryDeviceCall(
        byte deviceIndex,
        byte featureIndex,
        byte functionId,
        TimeSpan timeout,
        out HidppDeviceReply reply,
        params byte[] parameters)
    {
        lock (_gate)
        {
            reply = default;

            if (_longStream is null)
            {
                return false;
            }

            var request = HidppCodec.BuildLongRequest(deviceIndex, featureIndex, functionId, HidppCodec.SolaarSoftwareId, parameters);
            var expectedFunction = (byte)((functionId << 4) | HidppCodec.SolaarSoftwareId);

            try
            {
                _longStream.Write(request);
            }
            catch
            {
                return false;
            }

            var deadline = DateTime.UtcNow + timeout;
            var buffer = new byte[64];

            while (DateTime.UtcNow < deadline)
            {
                var remaining = Math.Max(MinimumReadTimeoutMs, (int)Math.Min(200, (deadline - DateTime.UtcNow).TotalMilliseconds));

                if (TryReadStream(_longStream, buffer, remaining, out var data)
                    && TryMatchDeviceReply(data, deviceIndex, featureIndex, expectedFunction, out reply))
                {
                    return true;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    break;
                }

                if (TryReadStream(_stream, buffer, MinimumReadTimeoutMs, out var shortData)
                    && TryMatchDeviceReply(shortData, deviceIndex, featureIndex, expectedFunction, out reply))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Read from the long channel (for the device notification listener).</summary>
    public bool TryReadRawLong(byte[] buffer, TimeSpan timeout, out byte[] data)
        => TryReadRawFrom(_longStream, buffer, timeout, out data);

    private static bool TryMatchDeviceReply(byte[] data, byte deviceIndex, byte featureIndex, byte functionByte, out HidppDeviceReply reply)
    {
        reply = default;

        if (!HidppCodec.TryParseDeviceReply(data, out var parsed)
            || parsed.DeviceIndex != deviceIndex
            || parsed.FeatureIndex != featureIndex
            || parsed.FunctionByte != functionByte)
        {
            return false;
        }

        reply = parsed;
        return true;
    }

    private bool TryReadStream(HidStream stream, byte[] buffer, int timeoutMs, out byte[] data)
    {
        data = [];
        stream.ReadTimeout = Math.Max(MinimumReadTimeoutMs, timeoutMs);

        try
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return false;
            }

            RaiseMessageRead(buffer, read);
            data = buffer.AsSpan(0, read).ToArray();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryReadRawFrom(HidStream? stream, byte[] buffer, TimeSpan timeout, out byte[] data)
    {
        data = [];

        if (stream is null)
        {
            return false;
        }

        lock (_gate)
        {
            return TryReadStream(stream, buffer, (int)timeout.TotalMilliseconds, out data);
        }
    }

    /// <summary>Background read: waits for a report for the given time and returns it as is (for the notification listener).</summary>
    public bool TryReadRaw(byte[] buffer, TimeSpan timeout, out byte[] data)
    {
        lock (_gate)
        {
            data = [];
            _stream.ReadTimeout = Math.Max(MinimumReadTimeoutMs, (int)timeout.TotalMilliseconds);

            try
            {
                var read = _stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return false;
                }

                RaiseMessageRead(buffer, read);
                data = buffer.AsSpan(0, read).ToArray();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Diagnostics: reads everything that has already accumulated in the input buffer.</summary>
    public void DrainInput()
    {
        lock (_gate)
        {
            var buffer = new byte[64];
            _stream.ReadTimeout = 5;

            for (var attempt = 0; attempt < 64; attempt++)
            {
                try
                {
                    var read = _stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    RaiseMessageRead(buffer, read);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    /// <summary>Diagnostics: sends a request and collects all replies within the allotted time.</summary>
    public IReadOnlyList<byte[]> CollectReplies(byte[] request, TimeSpan timeout, int maxReplies = 6)
    {
        lock (_gate)
        {
            var replies = new List<byte[]>();

            try
            {
                _stream.Write(request);
            }
            catch
            {
                return replies;
            }

            var buffer = new byte[64];
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline && replies.Count < maxReplies)
            {
                _stream.ReadTimeout = Math.Max(MinimumReadTimeoutMs, (int)Math.Min(400, (deadline - DateTime.UtcNow).TotalMilliseconds));

                try
                {
                    var read = _stream.Read(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        RaiseMessageRead(buffer, read);
                        replies.Add(buffer.AsSpan(0, read).ToArray());
                    }
                }
                catch (TimeoutException)
                {
                }
                catch
                {
                    break;
                }
            }

            return replies;
        }
    }

    /// <summary>Diagnostics: sends a raw report and returns the first reply as is.</summary>
    public bool TryRawExchange(byte[] request, TimeSpan timeout, out byte[] response, out string? error)
    {
        lock (_gate)
        {
            response = [];
            error = null;

            try
            {
                _stream.Write(request);
            }
            catch (Exception ex)
            {
                error = $"write: {ex.GetType().Name}: {ex.Message}";
                return false;
            }

            var buffer = new byte[64];
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                _stream.ReadTimeout = Math.Max(MinimumReadTimeoutMs, (int)Math.Min(400, (deadline - DateTime.UtcNow).TotalMilliseconds));

                try
                {
                    var read = _stream.Read(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        response = buffer.AsSpan(0, read).ToArray();
                        return true;
                    }
                }
                catch (TimeoutException)
                {
                }
                catch (Exception ex)
                {
                    error = $"read: {ex.GetType().Name}: {ex.Message}";
                    return false;
                }
            }

            error ??= Loc.Get("TimeoutNoReply");
            return false;
        }
    }

    /// <summary>
    /// All Logitech HID++ interfaces. For receivers, both channels are opened:
    /// the short one (0xFF00/0x0001) for the receiver and notifications, and the long one (0xFF00/0x0002) for devices.
    /// </summary>
    public static IReadOnlyList<HidppTransport> FindAll()
    {
        var shortEndpoints = new Dictionary<string, HidDevice>(StringComparer.OrdinalIgnoreCase);
        var longEndpoints = new Dictionary<string, HidDevice>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in DeviceList.Local.GetHidDevices(LogitechVendorId))
        {
            try
            {
                var endpoint = ClassifyVendorCollection(device);
                if (endpoint == 0)
                {
                    continue;
                }

                var key = GroupKey(device.DevicePath);
                (endpoint == HidppLongUsage ? longEndpoints : shortEndpoints)[key] = device;
            }
            catch
            {
                // The device may have disconnected between enumeration and descriptor parsing.
            }
        }

        var transports = new List<HidppTransport>();

        foreach (var (key, device) in shortEndpoints)
        {
            try
            {
                if (!device.TryOpen(out var stream))
                {
                    continue;
                }

                HidStream? longStream = null;
                if (longEndpoints.TryGetValue(key, out var longDevice) && longDevice.TryOpen(out var opened))
                {
                    longStream = opened;
                }

                transports.Add(new HidppTransport(device, stream, longStream, SafeProductName(device)));
            }
            catch
            {
                // The device may have disconnected between enumeration and opening.
            }
        }

        return transports;
    }

    /// <summary>0 — not a HID++ vendor collection; otherwise the usage (0x0001 short, 0x0002 long).</summary>
    private static int ClassifyVendorCollection(HidDevice device)
    {
        var descriptor = device.GetReportDescriptor();

        foreach (var item in descriptor.DeviceItems)
        {
            foreach (var value in item.Usages.GetAllValues())
            {
                if ((value >> 16) != HidppUsagePage)
                {
                    continue;
                }

                var usage = (int)(value & 0xFFFF);
                if (usage is HidppUsage or HidppLongUsage)
                {
                    return usage;
                }
            }
        }

        return 0;
    }

    /// <summary>Physical interface key: collection paths differ by the "&amp;colXX" suffix.</summary>
    private static string GroupKey(string path)
    {
        var index = path.IndexOf("&col", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? path : path[..index];
    }

    /// <summary>
    /// Sends a short request and waits for a reply from the same device.
    /// Unrelated reports (receiver notifications, etc.) are skipped.
    /// </summary>
    public bool TrySend(
        byte deviceIndex,
        byte featureIndex,
        byte functionId,
        byte softwareId,
        TimeSpan timeout,
        out HidppResponse response,
        byte parameter0 = 0,
        byte parameter1 = 0,
        byte parameter2 = 0)
    {
        lock (_gate)
        {
            response = default;

            var request = HidppCodec.BuildShortRequest(deviceIndex, featureIndex, functionId, softwareId, parameter0, parameter1, parameter2);
            var deadline = DateTime.UtcNow + timeout;
            var buffer = new byte[64];

            try
            {
                _stream.Write(request);
            }
            catch
            {
                return false;
            }

            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                _stream.ReadTimeout = Math.Max(MinimumReadTimeoutMs, (int)Math.Min(400, remaining.TotalMilliseconds));

                int read;
                try
                {
                    read = _stream.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch
                {
                    return false;
                }

                if (read <= 0)
                {
                    continue;
                }

                RaiseMessageRead(buffer, read);

                if (!HidppCodec.TryParseResponse(buffer.AsSpan(0, read), out var parsed))
                {
                    continue;
                }

                if (parsed.DeviceIndex != deviceIndex || parsed.SoftwareId != softwareId)
                {
                    continue;
                }

                if (!parsed.IsError && parsed.FeatureIndex != featureIndex)
                {
                    continue;
                }

                response = parsed;
                return true;
            }

            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _stream.Dispose();
        }
        catch
        {
            // Ignore.
        }
    }

    private static string SafeProductName(HidDevice device)
    {
        try
        {
            return device.GetProductName() ?? "Logitech";
        }
        catch
        {
            return "Logitech";
        }
    }

    private void RaiseMessageRead(byte[] buffer, int count)
    {
        var handler = MessageRead;
        if (handler is null || count <= 0)
        {
            return;
        }

        try
        {
            handler(buffer.AsSpan(0, count).ToArray());
        }
        catch
        {
        }
    }
}
