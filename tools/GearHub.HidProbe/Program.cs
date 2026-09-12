using System.Text;
using GearHub.Providers.Windows.Logitech;

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("=== Интерфейсы HID++ (usage 0xFF00/0x0001) ===");
var transports = HidppTransport.FindAll();

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
