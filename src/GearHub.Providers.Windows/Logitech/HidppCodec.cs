using GearHub.Core.Models;

namespace GearHub.Providers.Windows.Logitech;

/// <summary>Разобранный ответ HID++.</summary>
public readonly record struct HidppResponse(
    byte DeviceIndex,
    byte FeatureIndex,
    byte FunctionId,
    byte SoftwareId,
    bool IsError,
    byte ErrorCode,
    byte[] Parameters);

/// <summary>Низкоуровневые детали протокола HID++ 2.0: сборка запросов, разбор ответов, оценка заряда.</summary>
public static class HidppCodec
{
    public const byte ReportIdShort = 0x10;
    public const byte ReportIdLong = 0x11;
    public const byte ErrorFeatureIndex = 0xFF;

    /// <summary>Индекс для устройств, подключённых напрямую (Bluetooth, кабель).</summary>
    public const byte DeviceIndexDirect = 0xFF;

    public const int ShortReportLength = 7;
    public const int LongReportLength = 20;

    /// <summary>Короткий запрос: 7 байт, до 3 параметров.</summary>
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

    /// <summary>Огрублённый уровень из UNIFIED_BATTERY (0 = критично … 3 = полный).</summary>
    public static CoarseBatteryLevel MapLevel(byte level) => level switch
    {
        0 => CoarseBatteryLevel.Empty,
        1 => CoarseBatteryLevel.Low,
        2 => CoarseBatteryLevel.High,
        3 => CoarseBatteryLevel.Full,
        _ => CoarseBatteryLevel.Unknown,
    };

    /// <summary>
    /// Грубая оценка заряда по напряжению (кривая одноячеечного Li-ion).
    /// Для устройств на AA-батарейках значение будет завышенным — в UI это помечается как оценка.
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
