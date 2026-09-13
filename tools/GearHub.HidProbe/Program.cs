using System.Text;
using GearHub.Providers.Windows.Logitech;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--live"))
{
    Console.WriteLine("=== LIVE 40s: move the mouse and type on the keyboard! ===");
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

    Console.WriteLine("=== LIVE finished ===");
    return;
}

Console.WriteLine("=== HID++ interfaces (usage 0xFF00/0x0001) ===");
var transports = HidppTransport.FindAll();

Console.WriteLine();
Console.WriteLine("=== Receiver collections: usages and open ===");

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
Console.WriteLine("=== Receiver long-channel matrix: short and long requests ===");

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
            Console.WriteLine($"- pid={tag} usages={string.Join(",", vendorUsages.Select(usage => usage.ToString("X4")))}: OPEN FAILED");
            continue;
        }

        channel.ReadTimeout = 120;
        var outLength = device.GetMaxOutputReportLength();

        Console.WriteLine($"- pid={tag} usages={string.Join(",", vendorUsages.Select(usage => usage.ToString("X4")))} out={outLength}");
        ProbeChannel("ping", PadFrame([0x11, 0x01, 0x00, 0x1B, 0x00, 0x00, 0xA5], 20));
        var unified = ProbeChannel("getFeature 0x1004 (unified battery)", PadFrame([0x11, 0x01, 0x00, 0x0B, 0x10, 0x04, 0x00], 20));
        var legacy = ProbeChannel("getFeature 0x1000 (battery)", PadFrame([0x11, 0x01, 0x00, 0x0B, 0x10, 0x00, 0x00], 20));
        ProbeChannel("getFeature 0x1001 (battery voltage)", PadFrame([0x11, 0x01, 0x00, 0x0B, 0x10, 0x01, 0x00], 20));
        ProbeChannel("getFeature 0x0005 (device name)", PadFrame([0x11, 0x01, 0x00, 0x0B, 0x00, 0x05, 0x00], 20));
        ProbeChannel("getFeature 0x1814 (change host)", PadFrame([0x11, 0x01, 0x00, 0x0B, 0x18, 0x14, 0x00], 20));

        ProbeBatteryAt(unified, "0x1004");
        ProbeBatteryAt(legacy, "0x1000");

        channel.Dispose();

        void ProbeBatteryAt(List<string> featureReply, string featureName)
        {
            var index = ExtractFeatureIndex(featureReply);

            if (index <= 0)
            {
                return;
            }

            ProbeChannel($"{featureName} idx=0x{index:X2} func0 (caps)", PadFrame([0x11, 0x01, (byte)index, 0x0B, 0x00, 0x00, 0x00], 20));
            ProbeChannel($"{featureName} idx=0x{index:X2} func1 (status)", PadFrame([0x11, 0x01, (byte)index, 0x1B, 0x00, 0x00, 0x00], 20));
        }

        static int ExtractFeatureIndex(List<string> replies)
        {
            foreach (var reply in replies)
            {
                // Successful getFeature reply: 1101 00 0B <index> <type> <version> ...
                if (reply.StartsWith("1101000B", StringComparison.OrdinalIgnoreCase) && reply.Length >= 10)
                {
                    return Convert.ToInt32(reply.Substring(8, 2), 16);
                }
            }

            return 0;
        }

        List<string> ProbeChannel(string label, byte[] request)
        {
            DrainAll(channel);
            var collected = new List<string>();

            try
            {
                channel.Write(request);
            }
            catch (Exception error)
            {
                Console.WriteLine($"    {label}: write failed ({error.GetType().Name})");
                return collected;
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

            Console.WriteLine($"    {label}: {(collected.Count == 0 ? "no reply" : string.Join(" | ", collected))}");
            return collected;
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
    Console.WriteLine("Not found. If Logitech devices are connected, check whether the HID interface is busy with another app (G HUB etc.).");
}

foreach (var transport in transports)
{
    Console.WriteLine($"- {transport.ProductName} | receiver={transport.LooksLikeReceiver}");
    Console.WriteLine($"  {transport.DevicePath}");
}

Console.WriteLine();
Console.WriteLine("=== Experiment: slot 1, long read windows ===");

foreach (var transport in transports.Where(candidate => candidate.LooksLikeReceiver))
{
    Console.WriteLine($"--- {transport.ProductName} ---");

    transport.DrainInput();
    var featureReplies = transport.CollectReplies(
        HidppCodec.BuildShortRequest(1, 0x00, 0x00, 0x0B, 0x10, 0x04),
        TimeSpan.FromMilliseconds(3000),
        8);
    Console.WriteLine($"  getFeature(0x1004): {(featureReplies.Count == 0 ? "no reply" : string.Join(" | ", featureReplies.Select(Convert.ToHexString)))}");

    transport.DrainInput();
    var chargeReplies = transport.CollectReplies(RegisterRequest(1, 0x810D), TimeSpan.FromMilliseconds(3000), 8);
    Console.WriteLine($"  read 0x0D: {(chargeReplies.Count == 0 ? "no reply" : string.Join(" | ", chargeReplies.Select(Convert.ToHexString)))}");

    transport.DrainInput();
    var pingReplies = transport.CollectReplies(HidppCodec.BuildShortRequest(1, 0x00, 0x01, 0x0B, 0x00, 0x00, 0xA5), TimeSpan.FromMilliseconds(3000), 8);
    Console.WriteLine($"  ping: {(pingReplies.Count == 0 ? "no reply" : string.Join(" | ", pingReplies.Select(Convert.ToHexString)))}");
}

Console.WriteLine();

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
Console.WriteLine("=== Device index diagnostics (1-6) ===");

foreach (var transport in transports)
{
    Console.WriteLine($"--- {transport.ProductName} in={transport.MaxInputReportLength} out={transport.MaxOutputReportLength} ---");

    for (byte index = 1; index <= 6; index++)
    {
        // Root.getFeature(0x1004); swId = 0x0B, as in Solaar (high bit set).
        var featureRequest = HidppCodec.BuildShortRequest(index, 0x00, 0x00, 0x0B, 0x10, 0x04);
        var featureResult = Describe(transport, featureRequest);

        // Ping: [0x00, 0x1B, 0x00, 0x00, mark] — works for both HID++ 1.0 and 2.0.
        var pingRequest = HidppCodec.BuildShortRequest(index, 0x00, 0x01, 0x0B, 0x00, 0x00, 0xA5);
        var pingResult = Describe(transport, pingRequest);

        Console.WriteLine($"  idx={index}: feature={featureResult} | ping={pingResult}");
    }
}

Console.WriteLine();
Console.WriteLine("=== Second pass: 15 seconds, polling every second ===");
Console.WriteLine("Wiggle the mouse and press keys while polling runs!");

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

            // An 8F ... reply means "unreachable"; we are looking for a meaningful reply.
            if (raw.Length > 2 && raw[2] != 0x8F)
            {
                Console.WriteLine($"  REPLY FOUND: {transport.ProductName} idx={index}: {Convert.ToHexString(raw)}");
                found = true;
            }
        }
    }
}

if (!found)
{
    Console.WriteLine("  No meaningful replies were seen.");
}

Console.WriteLine();

static string Describe(HidppTransport transport, byte[] request, int timeoutMs = 700)
{
    var ok = transport.TryRawExchange(request, TimeSpan.FromMilliseconds(timeoutMs), out var raw, out var error);
    return ok ? Convert.ToHexString(raw) : $"no reply ({error})";
}

static IEnumerable<(string Label, byte[] Frame)> BuildDevProbeMatrix(int length)
{
    var shortFrame = new byte[length];
    shortFrame[0] = 0x10;
    shortFrame[1] = 0x01;
    shortFrame[2] = 0x00;
    shortFrame[3] = 0x0B;
    shortFrame[4] = 0x10;
    shortFrame[5] = 0x04;
    yield return ("HID++ short 0x10 getFeature", shortFrame);

    foreach (var deviceIndex in new byte[] { 0x01, 0xFF })
    {
        var longFrame = new byte[length];
        longFrame[0] = 0x11;
        longFrame[1] = deviceIndex;
        longFrame[2] = 0x00;
        longFrame[3] = 0x0B;
        longFrame[4] = 0x10;
        longFrame[5] = 0x04;
        yield return ($"HID++ long 0x11 dev=0x{deviceIndex:X2} getFeature", longFrame);
    }
}

static byte[] BuildCenturionFrame(byte reportId, byte address, byte functionByte, byte featureIndex, byte[] parameters, int frameLength)
{
    var frame = new byte[frameLength];
    var offset = 0;

    if (reportId == 0x51)
    {
        frame[offset++] = 0x51;
        frame[offset++] = (byte)(3 + parameters.Length);
        frame[offset++] = 0x00;
    }
    else
    {
        frame[offset++] = 0x50;
        frame[offset++] = address;
        frame[offset++] = (byte)(3 + parameters.Length);
        frame[offset++] = 0x00;
    }

    frame[offset++] = featureIndex;
    frame[offset++] = functionByte;
    Array.Copy(parameters, 0, frame, offset, parameters.Length);
    return frame;
}

static bool TryUnwrapCenturion(byte[] data, out byte reportId, out byte address, out byte[] inner)
{
    reportId = data.Length > 0 ? data[0] : (byte)0;
    address = 0;
    inner = [];

    if (data.Length >= 4 && data[0] == 0x50)
    {
        var cpl = data[2];
        if (cpl < 2 || 3 + cpl > data.Length)
        {
            return false;
        }

        address = data[1];
        inner = data[4..(3 + cpl)];
        return true;
    }

    if (data.Length >= 3 && data[0] == 0x51)
    {
        var cpl = data[1];
        if (cpl < 2 || 2 + cpl > data.Length)
        {
            return false;
        }

        inner = data[3..(2 + cpl)];
        return true;
    }

    return false;
}

static byte[]? CenturionCall(HidppTransport transport, int length, byte reportId, byte address, string label, byte featureIndex, byte functionByte, byte[] parameters)
{
    var frame = BuildCenturionFrame(reportId, address, functionByte, featureIndex, parameters, length);

    foreach (var read in transport.CollectReplies(frame, TimeSpan.FromMilliseconds(700), 8))
    {
        if (!TryUnwrapCenturion(read, out _, out _, out var inner))
        {
            continue;
        }

        if (inner.Length >= 2 && inner[0] == featureIndex && inner[1] == functionByte)
        {
            Console.WriteLine($"    {label}: {Convert.ToHexString(inner)}");
            return inner;
        }
    }

    Console.WriteLine($"    {label}: no reply");
    return null;
}

Console.WriteLine();
Console.WriteLine("=== Centurion (G435 and similar dongles) ===");

foreach (var transport in transports.Where(candidate => !candidate.LooksLikeReceiver && candidate.MaxOutputReportLength >= 64))
{
    var length = transport.MaxOutputReportLength;
    Console.WriteLine($"--- {transport.ProductName} out={length} ---");

    // Passive listening: what the dongle sends on its own (headset telemetry).
    Console.WriteLine("  listening for 25 seconds...");
    var listenBuffer = new byte[128];
    var listenUntil = DateTimeOffset.UtcNow.AddSeconds(25);
    while (DateTimeOffset.UtcNow < listenUntil)
    {
        if (transport.TryReadRaw(listenBuffer, TimeSpan.FromMilliseconds(200), out var heard))
        {
            Console.WriteLine($"    read[{heard.Length}]: {Convert.ToHexString(heard)}");
        }
    }

    byte reportId = 0;
    byte address = 0;
    var centurionFound = false;

    // Variant 0x51: no address byte. Solaar uses ROOT.GetProtocolVersion (func 0x10) for discovery.
    foreach (var function in new byte[] { 0x10, 0x1B })
    {
        var parameters = function == 0x1B ? new byte[] { 0x00, 0x00, 0xA5 } : new byte[] { 0x00, 0x00, 0x00 };
        var probe = BuildCenturionFrame(0x51, 0x00, function, 0x00, parameters, length);

        foreach (var read in transport.CollectReplies(probe, TimeSpan.FromMilliseconds(600), 8))
        {
            if (TryUnwrapCenturion(read, out var id, out var addr, out var inner))
            {
                var match = inner.Length >= 2 && inner[0] == 0x00 && inner[1] == function;
                Console.WriteLine($"  0x51 func=0x{function:X2}: [{Convert.ToHexString(read)}] match={match}");

                if (match)
                {
                    reportId = id;
                    address = addr;
                    centurionFound = true;
                }
            }
            else
            {
                Console.WriteLine($"  0x51 func=0x{function:X2}: raw read [{Convert.ToHexString(read)}]");
            }
        }
    }

    // Variant 0x50: the address byte is brute-forced (Solaar: probe device_addr 0x00-0xFF).
    if (!centurionFound)
    {
        var notified = false;

        for (var candidate = 0; candidate < 256 && !centurionFound; candidate++)
        {
            var ping = BuildCenturionFrame(0x50, (byte)candidate, 0x10, 0x00, [0x00, 0x00, 0x00], length);
            foreach (var read in transport.CollectReplies(ping, TimeSpan.FromMilliseconds(20), 4))
            {
                if (TryUnwrapCenturion(read, out var id, out var addr, out var inner)
                    && inner.Length >= 2 && inner[0] == 0x00 && inner[1] == 0x10)
                {
                    Console.WriteLine($"  0x50: address found 0x{candidate:X2}, reply [{Convert.ToHexString(read)}]");
                    reportId = id;
                    address = addr;
                    centurionFound = true;
                    break;
                }

                if (!notified)
                {
                    notified = true;
                    Console.WriteLine($"  a read arrived during the scan [{Convert.ToHexString(read)}]");
                }
            }
        }

        if (!centurionFound)
        {
            Console.WriteLine("  0x50: the device did not answer on any address");
        }
    }

    // Standard HID++ in 65-byte frames: maybe the dongle speaks via 0x10/0x11 reports.
    foreach (var (label, frame) in BuildDevProbeMatrix(length))
    {
        var ok = transport.TryRawExchange(frame, TimeSpan.FromMilliseconds(700), out var rawReply, out var rawError);
        Console.WriteLine($"  {label}: {(ok ? Convert.ToHexString(rawReply) : $"no reply ({rawError})")}");
    }

    if (!centurionFound)
    {
        continue;
    }

    Console.WriteLine($"  link established: report=0x{reportId:X2} addr=0x{address:X2}");

    var fs = CenturionCall(transport, length, reportId, address, "getFeature(0x0001)", 0x00, 0x0B, [0x00, 0x01]);
    var fsIndex = fs is { Length: >= 3 } ? fs[2] : (byte)0;

    if (fsIndex == 0)
    {
        Console.WriteLine("  FEATURE_SET not found");
        continue;
    }

    var countReply = CenturionCall(transport, length, reportId, address, "count", fsIndex, 0x0B, []);
    var count = countReply is { Length: >= 3 } ? countReply[2] : (byte)0;
    Console.WriteLine($"  dongle features: {count}");

    var batteryFeature = (byte)0;
    var sawBridge = false;

    for (byte index = 0; index < Math.Min((int)count, 24); index++)
    {
        var reply = CenturionCall(transport, length, reportId, address, $"feature#{index}", fsIndex, 0x1B, [index]);
        if (reply is not { Length: >= 5 })
        {
            continue;
        }

        var featureId = (ushort)((reply[3] << 8) | reply[4]);
        Console.WriteLine($"    idx={index}: feature=0x{featureId:X4}");

        if (featureId == 0x0104)
        {
            batteryFeature = index;
        }
        else if (featureId == 0x0003)
        {
            sawBridge = true;
        }
    }

    if (batteryFeature != 0)
    {
        var soc = CenturionCall(transport, length, reportId, address, "BATTERY_SOC", batteryFeature, 0x0B, []);
        Console.WriteLine(soc is { Length: >= 3 }
            ? $"  charge: {soc[2]}% (status {(soc.Length > 4 ? soc[4] : (byte)0)})"
            : "  battery: the feature exists but did not reply");
    }
    else
    {
        Console.WriteLine(sawBridge
            ? "  battery: the dongle has no BATTERY_SOC, but bridge 0x0003 exists — charge is read through the headset bridge"
            : "  battery: BATTERY_SOC (0x0104) not found");
    }
}

Console.WriteLine();
Console.WriteLine("=== Devices and charge ===");

using var provider = new LogitechHidppProvider();
var devices = await provider.DiscoverAsync(CancellationToken.None);

if (devices.Count == 0)
{
    Console.WriteLine("Nothing found. A device behind the receiver may be asleep — press a button / move the mouse and retry.");
}

foreach (var device in devices)
{
    var battery = device.Battery.Percent is { } percent
        ? $"{percent}%"
        : device.Battery.Coarse.ToString();

    Console.WriteLine($"- {device.Name} [{device.Kind}] charge={battery} fault={device.HasFault} {device.Detail}");
    Console.WriteLine($"  {device.DeviceId}");
}
