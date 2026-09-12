using HidSharp;

namespace GearHub.Providers.Windows.Logitech;

/// <summary>
/// Транспорт HID++: короткий канал приёмника (usage 0xFF00/0x0001, отчёты 0x10 — регистры и
/// нотификации) и длинный канал устройств (usage 0xFF00/0x0002, отчёты 0x11 — HID++ 2.0-запросы).
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
    /// Любое прочитанное сообщение, включая нотификации, которые не являются ответом на запрос.
    /// Провайдер через это событие видит то, что иначе было бы отброшено при разборе ответов.
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
    /// Длинный канал (usage 0xFF00/0x0002): только через него устройства за приёмником отвечают
    /// на HID++ 2.0-запросы длинными кадрами 0x11. Есть у приёмников Unifying/Bolt.
    /// </summary>
    public bool HasDeviceChannel => _longStream is not null;

    public int MaxInputReportLength => _device.GetMaxInputReportLength();

    public int MaxOutputReportLength => _device.GetMaxOutputReportLength();

    /// <summary>Приёмники Unifying/Bolt представляются как «USB Receiver» и проксируют до 6 устройств.</summary>
    public bool LooksLikeReceiver => ProductName.Contains("receiver", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Отправляет запрос и возвращает первый ответ, адресованный этому же устройству.
    /// Подходит для HID++ 1.0-запросов (регистры), где нет SoftwareId.
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
    /// HID++ 2.0-запрос устройству за приёмником: длинный кадр 0x11 в канал 0xFF00/0x0002.
    /// Ответ ищется по индексу устройства, фиче и байту функции (function &lt;&lt; 4 | 0x0B),
    /// включая кадр ошибки 0xFF. Пока ждём, продолжаем читать и короткий канал (нотификации).
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

    /// <summary>Чтение из длинного канала (для слушателя нотификаций устройств).</summary>
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

    /// <summary>Фоновое чтение: ждёт отчёт заданное время и возвращает его как есть (для слушателя нотификаций).</summary>
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

    /// <summary>Диагностика: читает всё, что уже накопилось во входном буфере.</summary>
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

    /// <summary>Диагностика: отправляет запрос и собирает все ответы за отведённое время.</summary>
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

    /// <summary>Диагностика: отправляет сырой отчёт и возвращает первый ответ как есть.</summary>
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

            error ??= "таймаут: ответа нет";
            return false;
        }
    }

    /// <summary>
    /// Все HID++-интерфейсы Logitech. Для приёмников открываются оба канала:
    /// короткий (0xFF00/0x0001) для приёмника и нотификаций и длинный (0xFF00/0x0002) для устройств.
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
                // Устройство могло отключиться между перечислением и разбором дескриптора.
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
                // Устройство могло отключиться между перечислением и открытием.
            }
        }

        return transports;
    }

    /// <summary>0 — не vendor-коллекция HID++; иначе usage (0x0001 короткий, 0x0002 длинный).</summary>
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

    /// <summary>Ключ физического интерфейса: пути коллекций отличаются хвостом «&amp;colXX».</summary>
    private static string GroupKey(string path)
    {
        var index = path.IndexOf("&col", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? path : path[..index];
    }

    /// <summary>
    /// Отправляет короткий запрос и ждёт ответ от того же устройства.
    /// Посторонние отчёты (нотификации приёмника и т.п.) пропускаются.
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
