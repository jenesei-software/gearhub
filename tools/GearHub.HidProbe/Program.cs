using System.Text;
using GearHub.Providers.Windows.Logitech;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--live"))
{
    Console.WriteLine("=== LIVE 40s: двигай мышь и стучи по клавиатуре! ===");
    var liveTransports = HidppTransport.FindAll().ToList();
    var stop = DateTimeOffset.UtcNow.AddSeconds(40);
    var startAt = DateTimeOffset.UtcNow;
    var buffer = new byte[64];
    var ask = 0;

    while (DateTimeOffset.UtcNow < stop)
    {
        foreach (var transport in liveTransports)
        {
            var seconds = (int)(DateTimeOffset.UtcNow - startAt).TotalSeconds;
            var shortName = transport.DevicePath.Contains("c52b", StringComparison.OrdinalIgnoreCase) ? "C52B"
                : transport.DevicePath.Contains("c548", StringComparison.OrdinalIgnoreCase) ? "C548"
                : transport.ProductName;

            var request = ask % 2 == 0
                ? HidppCodec.BuildShortRequest(1, 0x00, 0x00, 0x0B, 0x10, 0x04)
                : RegisterRequest(1, 0x810D);

            foreach (var reply in transport.CollectReplies(request, TimeSpan.FromMilliseconds(250), 6))
            {
                Console.WriteLine($"[{seconds,3}s] {shortName} ASK#{ask} -> {Convert.ToHexString(reply)}");
            }

            while (transport.TryReadRaw(buffer, TimeSpan.FromMilliseconds(60), out var passive))
            {
                Console.WriteLine($"[{seconds,3}s] {shortName} PASSIVE {Convert.ToHexString(passive)}");
            }
        }

        ask++;
    }

    Console.WriteLine("=== LIVE закончен ===");
    return;
}

Console.WriteLine("=== Интерфейсы HID++ (usage 0xFF00/0x0001) ===");
var transports = HidppTransport.FindAll();

Console.WriteLine();
Console.WriteLine("=== Коллекции приёмников: usages и открытие ===");

foreach (var device in HidSharp.DeviceList.Local.GetHidDevices(0x046D))
{
    try
    {
        var descriptor = device.GetReportDescriptor();
        var usages = new List<string>();

        foreach (var item in descriptor.DeviceItems)
        {
            foreach (var value in item.Usages.GetAllValues())
            {
                usages.Add($"{value >> 16:X4}:{value & 0xFFFF:X4}");
            }
        }

        var product = string.Empty;
        try
        {
            product = device.GetProductName() ?? string.Empty;
        }
        catch
        {
        }

        var opened = device.TryOpen(out var probeStream);
        probeStream?.Dispose();

        Console.WriteLine($"- {product} in={device.GetMaxInputReportLength()} out={device.GetMaxOutputReportLength()} open={opened}");
        Console.WriteLine($"  {device.DevicePath}");
        Console.WriteLine($"  usages: {string.Join(", ", usages.Distinct())}");
    }
    catch
    {
    }
}

Console.WriteLine();
Console.WriteLine("=== Матрица длинных каналов приёмников: короткий и длинный запросы ===");

foreach (var device in HidSharp.DeviceList.Local.GetHidDevices(0x046D))
{
    try
    {
        var descriptor = device.GetReportDescriptor();
        var vendorUsages = new List<int>();

        foreach (var item in descriptor.DeviceItems)
        {
            foreach (var value in item.Usages.GetAllValues())
            {
                if ((value >> 16) == 0xFF00)
                {
                    vendorUsages.Add((int)(value & 0xFFFF));
                }
            }
        }

        if (vendorUsages.Count == 0 || vendorUsages.All(usage => usage == 0x0001))
        {
            continue;
        }

        var tag = device.DevicePath;
        var pidIndex = tag.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
        tag = pidIndex >= 0 && tag.Length >= pidIndex + 8 ? tag.Substring(pidIndex + 4, 4) : tag;

        if (!device.TryOpen(out var channel))
        {
            Console.WriteLine($"- pid={tag} usages={string.Join(",", vendorUsages.Select(usage => usage.ToString("X4")))}: ОТКРЫТЬ НЕ УДАЛОСЬ");
            continue;
        }

        channel.ReadTimeout = 120;
        var outLength = device.GetMaxOutputReportLength();

        Console.WriteLine($"- pid={tag} usages={string.Join(",", vendorUsages.Select(usage => usage.ToString("X4")))} out={outLength}");
        ProbeChannel("короткий 0x10 getFeature", [0x10, 0x01, 0x00, 0x0B, 0x10, 0x04, 0x00]);
        ProbeChannel("короткий 0x10 reg 0x0D", [0x10, 0x01, 0x81, 0x0D, 0x00, 0x00, 0x00]);
        ProbeChannel("длинный 0x11/20 getFeature", PadFrame([0x11, 0x01, 0x00, 0x0B, 0x10, 0x04, 0x00], 20));
        ProbeChannel("длинный 0x11/20 reg 0x0D", PadFrame([0x11, 0x01, 0x81, 0x0D, 0x00, 0x00, 0x00], 20));

        if (outLength > 20)
        {
            ProbeChannel($"длинный 0x11/{outLength} getFeature", PadFrame([0x11, 0x01, 0x00, 0x0B, 0x10, 0x04, 0x00], outLength));
        }

        channel.Dispose();

        void ProbeChannel(string label, byte[] request)
        {
            DrainAll(channel);
            var collected = new List<string>();

            try
            {
                channel.Write(request);
            }
            catch (Exception error)
            {
                Console.WriteLine($"    {label}: запись не удалась ({error.GetType().Name})");
                return;
            }

            var until = DateTimeOffset.UtcNow.AddMilliseconds(1800);
            var buffer = new byte[64];

            while (DateTimeOffset.UtcNow < until)
            {
                try
                {
                    var count = channel.Read(buffer, 0, buffer.Length);
                    if (count > 0)
                    {
                        collected.Add(Convert.ToHexString(buffer.AsSpan(0, count)));
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

            Console.WriteLine($"    {label}: {(collected.Count == 0 ? "нет ответа" : string.Join(" | ", collected))}");
        }

        static void DrainAll(HidSharp.HidStream channelToDrain)
        {
            var scratch = new byte[64];

            while (true)
            {
                try
                {
                    if (channelToDrain.Read(scratch, 0, scratch.Length) <= 0)
                    {
                        return;
                    }
                }
                catch
                {
                    return;
                }
            }
        }

        static byte[] PadFrame(byte[] frame, int length)
        {
            var padded = new byte[length];
            Array.Copy(frame, padded, Math.Min(frame.Length, length));
            return padded;
        }
    }
    catch
    {
    }
}

if (transports.Count == 0)
{
    Console.WriteLine("Не найдено. Если Logitech-устройства подключены — проверьте, не занят ли HID-интерфейс другой программой (G HUB и т.п.).");
}

foreach (var transport in transports)
{
    Console.WriteLine($"- {transport.ProductName} | приёмник={transport.LooksLikeReceiver}");
    Console.WriteLine($"  {transport.DevicePath}");
}

Console.WriteLine();
Console.WriteLine("=== Эксперимент: слот 1, длинные окна чтения ===");

foreach (var transport in transports.Where(candidate => candidate.LooksLikeReceiver))
{
    Console.WriteLine($"--- {transport.ProductName} ---");

    transport.DrainInput();
    var featureReplies = transport.CollectReplies(
        HidppCodec.BuildShortRequest(1, 0x00, 0x00, 0x0B, 0x10, 0x04),
        TimeSpan.FromMilliseconds(3000),
        8);
    Console.WriteLine($"  getFeature(0x1004): {(featureReplies.Count == 0 ? "нет ответа" : string.Join(" | ", featureReplies.Select(Convert.ToHexString)))}");

    transport.DrainInput();
    var chargeReplies = transport.CollectReplies(RegisterRequest(1, 0x810D), TimeSpan.FromMilliseconds(3000), 8);
    Console.WriteLine($"  read 0x0D: {(chargeReplies.Count == 0 ? "нет ответа" : string.Join(" | ", chargeReplies.Select(Convert.ToHexString)))}");

    transport.DrainInput();
    var pingReplies = transport.CollectReplies(HidppCodec.BuildShortRequest(1, 0x00, 0x01, 0x0B, 0x00, 0x00, 0xA5), TimeSpan.FromMilliseconds(3000), 8);
    Console.WriteLine($"  ping: {(pingReplies.Count == 0 ? "нет ответа" : string.Join(" | ", pingReplies.Select(Convert.ToHexString)))}");
}

Console.WriteLine();

static string Collect(HidppTransport transport, byte[] request, int timeoutMs)
{
    var replies = transport.CollectReplies(request, TimeSpan.FromMilliseconds(timeoutMs));
    return replies.Count == 0
        ? "нет ответа"
        : string.Join(" | ", replies.Select(Convert.ToHexString));
}

static byte[] RegisterRequest(byte deviceIndex, ushort requestId, params byte[] parameters)
{
    var buffer = new byte[7];
    buffer[0] = 0x10;
    buffer[1] = deviceIndex;
    buffer[2] = (byte)(requestId >> 8);
    buffer[3] = (byte)(requestId & 0xFF);

    for (var index = 0; index < parameters.Length && index < 3; index++)
    {
        buffer[4 + index] = parameters[index];
    }

    return buffer;
}

Console.WriteLine();
Console.WriteLine("=== Диагностика по индексам устройств (1-6) ===");

foreach (var transport in transports)
{
    Console.WriteLine($"--- {transport.ProductName} in={transport.MaxInputReportLength} out={transport.MaxOutputReportLength} ---");

    for (byte index = 1; index <= 6; index++)
    {
        // Root.getFeature(0x1004); swId = 0x0B, как в Solaar (старший бит установлен).
        var featureRequest = HidppCodec.BuildShortRequest(index, 0x00, 0x00, 0x0B, 0x10, 0x04);
        var featureResult = Describe(transport, featureRequest);

        // Ping: [0x00, 0x1B, 0x00, 0x00, mark] — работает и для HID++ 1.0, и для 2.0.
        var pingRequest = HidppCodec.BuildShortRequest(index, 0x00, 0x01, 0x0B, 0x00, 0x00, 0xA5);
        var pingResult = Describe(transport, pingRequest);

        Console.WriteLine($"  idx={index}: feature={featureResult} | ping={pingResult}");
    }
}

Console.WriteLine();
Console.WriteLine("=== Второй проход: 15 секунд, опрос каждую секунду ===");
Console.WriteLine("Волнуйтесь мышью и понажимайте клавиши, пока идёт опрос!");

var deadline = DateTime.UtcNow.AddSeconds(15);
var found = false;

while (DateTime.UtcNow < deadline && !found)
{
    foreach (var transport in transports)
    {
        for (byte index = 1; index <= 6; index++)
        {
            var request = HidppCodec.BuildShortRequest(index, 0x00, 0x00, 0x0B, 0x10, 0x04);
            if (!transport.TryRawExchange(request, TimeSpan.FromMilliseconds(150), out var raw, out _))
            {
                continue;
            }

            // Ответ 8F ... — ошибка «недостижимо»; ищем содержательный ответ.
            if (raw.Length > 2 && raw[2] != 0x8F)
            {
                Console.WriteLine($"  ЕСТЬ ОТВЕТ: {transport.ProductName} idx={index}: {Convert.ToHexString(raw)}");
                found = true;
            }
        }
    }
}

if (!found)
{
    Console.WriteLine("  Содержательных ответов не было.");
}

Console.WriteLine();

static string Describe(HidppTransport transport, byte[] request, int timeoutMs = 700)
{
    var ok = transport.TryRawExchange(request, TimeSpan.FromMilliseconds(timeoutMs), out var raw, out var error);
    return ok ? Convert.ToHexString(raw) : $"нет ответа ({error})";
}

Console.WriteLine();
Console.WriteLine("=== Centurion (G435 и подобные донглы) ===");

foreach (var transport in transports.Where(candidate => !candidate.LooksLikeReceiver))
{
    Console.WriteLine($"--- {transport.ProductName} ---");

    foreach (var reportId in new byte[] { 0x51, 0x50 })
    {
        foreach (var length in new[] { 64, 65 })
        {
            var frame = new byte[length];
            frame[0] = reportId;

            if (reportId == 0x51)
            {
                frame[1] = 6;
                frame[2] = 0x00;
                frame[3] = 0x00;
                frame[4] = 0x10;
            }
            else
            {
                frame[1] = 0x00;
                frame[2] = 6;
                frame[3] = 0x00;
                frame[4] = 0x00;
                frame[5] = 0x10;
            }

            transport.DrainInput();
            var rawOk = transport.TryRawExchange(frame, TimeSpan.FromMilliseconds(700), out var rawReply, out var rawError);
            var display = rawOk ? Convert.ToHexString(rawReply) : $"нет ответа ({rawError})";
            Console.WriteLine($"  0x{reportId:X2} len={length}: {display}");
        }
    }
}

Console.WriteLine();
Console.WriteLine("=== Устройства и заряд ===");

using var provider = new LogitechHidppProvider();
var devices = await provider.DiscoverAsync(CancellationToken.None);

if (devices.Count == 0)
{
    Console.WriteLine("Ничего не найдено. Устройство за приёмником может спать — нажмите кнопку/подвиньте мышь и повторите.");
}

foreach (var device in devices)
{
    var battery = device.Battery.Percent is { } percent
        ? $"{percent}%"
        : device.Battery.Coarse.ToString();

    Console.WriteLine($"- {device.Name} [{device.Kind}] заряд={battery} ошибка={device.HasFault} {device.Detail}");
    Console.WriteLine($"  {device.DeviceId}");
}
