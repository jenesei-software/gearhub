using System.Runtime.InteropServices;

namespace GearHub.Providers.Windows.XInput;

/// <summary>P/Invoke wrapper over XInput 1.4 — provides access to Xbox gamepads and their charge.</summary>
internal static class XInputNative
{
    public const int MaxSlots = 4;

    public const byte DevTypeGamepad = 0x00;

    public const byte BatteryTypeDisconnected = 0x00;
    public const byte BatteryTypeWired = 0x01;
    public const byte BatteryTypeAlkaline = 0x02;
    public const byte BatteryTypeNiMh = 0x03;

    public const byte BatteryLevelEmpty = 0x00;
    public const byte BatteryLevelLow = 0x01;
    public const byte BatteryLevelMedium = 0x02;
    public const byte BatteryLevelFull = 0x03;

    private const int ErrorSuccess = 0;

    private static readonly bool Available = NativeLibrary.TryLoad("xinput1_4.dll", out _);

    public static bool IsAvailable => Available;

    public static bool TryGetState(int index, out XInputState state)
    {
        state = default;
        return Available && XInputGetState(index, ref state) == ErrorSuccess;
    }

    public static bool TryGetBatteryInfo(int index, out XInputBatteryInformation info)
    {
        info = default;
        return Available && XInputGetBatteryInformation(index, DevTypeGamepad, ref info) == ErrorSuccess;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(int dwUserIndex, ref XInputState pState);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern int XInputGetBatteryInformation(int dwUserIndex, byte devType, ref XInputBatteryInformation pBatteryInformation);
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputState
{
    public uint PacketNumber;
    public XInputGamepad Gamepad;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputGamepad
{
    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short LeftThumbX;
    public short LeftThumbY;
    public short RightThumbX;
    public short RightThumbY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputBatteryInformation
{
    public byte BatteryType;
    public byte BatteryLevel;
}
