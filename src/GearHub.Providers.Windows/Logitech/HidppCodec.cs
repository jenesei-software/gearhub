using GearHub.Core.Models;

namespace GearHub.Providers.Windows.Logitech;

/// <summary>Parsed HID++ response.</summary>
public readonly record struct HidppResponse(
    byte DeviceIndex,
    byte FeatureIndex,
    byte FunctionId,
    byte SoftwareId,
    bool IsError,
    byte ErrorCode,
    byte[] Parameters);

/// <summary>Response from a device behind the receiver (long channel). Error frame: [0xFF, feature, func_sw, error].</summary>
public readonly record struct HidppDeviceReply(
    byte DeviceIndex,
    byte FeatureIndex,
    byte FunctionByte,
    bool IsError,
    byte ErrorCode,
    byte[] Payload);

/// <summary>Low-level details of the HID++ 2.0 protocol: building requests, parsing replies, estimating charge.</summary>
public static class HidppCodec
{
    public const byte ReportIdShort = 0x10;
    public const byte ReportIdLong = 0x11;
    public const byte ErrorFeatureIndex = 0xFF;

    /// <summary>Index for devices connected directly (Bluetooth, cable).</summary>
    public const byte DeviceIndexDirect = 0xFF;

    public const int ShortReportLength = 7;
    public const int LongReportLength = 20;

    /// <summary>Short request: 7 bytes, up to 3 parameters.</summary>
    public static byte[] BuildShortRequest(
        byte deviceIndex,
        byte featureIndex,
        byte functionId,
        byte softwareId,
        byte parameter0 = 0,
        byte parameter1 = 0,
        byte parameter2 = 0) =>
    [
        ReportIdShort,
        deviceIndex,
        featureIndex,
        (byte)((functionId << 4) | softwareId),
        parameter0,
        parameter1,
        parameter2,
    ];

    /// <summary>SoftwareId as in Solaar: high bit set to distinguish responses from notifications (swId = 0).</summary>
    public const byte SolaarSoftwareId = 0x0B;

    /// <summary>
    /// Long request: 20 bytes, up to 16 parameters. Devices behind Unifying/Bolt receivers
    /// respond only to long frames 0x11 sent to the usage collection 0xFF00/0x0002.
    /// </summary>
    public static byte[] BuildLongRequest(
        byte deviceIndex,
        byte featureIndex,
        byte functionId,
        byte softwareId,
        params byte[] parameters)
    {
        var frame = new byte[LongReportLength];
        frame[0] = ReportIdLong;
        frame[1] = deviceIndex;
        frame[2] = featureIndex;
        frame[3] = (byte)((functionId << 4) | softwareId);

        var count = Math.Min(parameters.Length, frame.Length - 4);
        Array.Copy(parameters, 0, frame, 4, count);
        return frame;
    }

    /// <summary>Parses a device reply (successful or the 0xFF error frame).</summary>
    public static bool TryParseDeviceReply(ReadOnlySpan<byte> report, out HidppDeviceReply reply)
    {
        reply = default;

        if (report.Length < 5)
        {
            return false;
        }

        var reportId = report[0];
        if (reportId is not (ReportIdShort or ReportIdLong))
        {
            return false;
        }

        if (report[2] == ErrorFeatureIndex)
        {
            // HID++ 2.0 error: [report, devIdx, 0xFF, feature, func_sw, error, ...].
            if (report.Length < 6)
            {
                return false;
            }

            reply = new HidppDeviceReply(
                DeviceIndex: report[1],
                FeatureIndex: report[3],
                FunctionByte: report[4],
                IsError: true,
                ErrorCode: report[5],
                Payload: report[6..].ToArray());
            return true;
        }

        reply = new HidppDeviceReply(
            DeviceIndex: report[1],
            FeatureIndex: report[2],
            FunctionByte: report[3],
            IsError: false,
            ErrorCode: 0,
            Payload: report[4..].ToArray());
        return true;
    }

    public static bool TryParseResponse(ReadOnlySpan<byte> report, out HidppResponse response)
    {
        response = default;

        if (report.Length < ShortReportLength)
        {
            return false;
        }

        var reportId = report[0];
        if (reportId is not (ReportIdShort or ReportIdLong))
        {
            return false;
        }

        var length = reportId == ReportIdLong ? LongReportLength : ShortReportLength;
        if (report.Length < length)
        {
            length = report.Length;
        }

        var featureIndex = report[2];
        var isError = featureIndex == ErrorFeatureIndex;

        response = new HidppResponse(
            DeviceIndex: report[1],
            FeatureIndex: featureIndex,
            FunctionId: (byte)(report[3] >> 4),
            SoftwareId: (byte)(report[3] & 0x0F),
            IsError: isError,
            ErrorCode: isError && report.Length > 4 ? report[4] : (byte)0,
            Parameters: report.Slice(4, length - 4).ToArray());
        return true;
    }

    /// <summary>Coarse level from UNIFIED_BATTERY (0 = critical … 3 = full).</summary>
    public static CoarseBatteryLevel MapLevel(byte level) => level switch
    {
        0 => CoarseBatteryLevel.Empty,
        1 => CoarseBatteryLevel.Low,
        2 => CoarseBatteryLevel.High,
        3 => CoarseBatteryLevel.Full,
        _ => CoarseBatteryLevel.Unknown,
    };

    /// <summary>
    /// Rough charge estimate from voltage (single-cell Li-ion curve).
    /// For devices running on AA batteries the value will be overestimated — the UI marks this as an estimate.
    /// </summary>
    public static int EstimatePercentFromMillivolts(int millivolts)
    {
        foreach (var (threshold, percent) in VoltageCurve)
        {
            if (millivolts >= threshold)
            {
                return percent;
            }
        }

        return 0;
    }

    private static readonly (int Millivolts, int Percent)[] VoltageCurve =
    [
        (4100, 100),
        (4000, 90),
        (3900, 80),
        (3850, 70),
        (3800, 60),
        (3750, 50),
        (3720, 40),
        (3700, 30),
        (3680, 25),
        (3650, 20),
        (3600, 15),
        (3500, 10),
        (3400, 5),
        (0, 0),
    ];
}
