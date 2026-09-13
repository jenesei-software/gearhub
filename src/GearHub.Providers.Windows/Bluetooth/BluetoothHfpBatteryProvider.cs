using System.Runtime.InteropServices;
using System.Text;
using GearHub.Core.Abstractions;
using GearHub.Core.Models;
using GearHub.Core.Services;

namespace GearHub.Providers.Windows.Bluetooth;

/// <summary>
/// Classic Bluetooth audio devices (Hands-Free profile): Windows itself receives the battery level
/// from the device over HFP and stores it as the DEVPKEY_Device_BatteryLevel property on the
/// "Hands-Free AG" service instance. Reading that property gives the real charge without touching
/// the audio link — the headset stays connected to Windows while we only read the cached value.
/// Headsets like the Logitech G435 report their charge this way when connected over Bluetooth
/// (their Lightspeed dongle never broadcasts it).
/// </summary>
public sealed class BluetoothHfpBatteryProvider : IGearProvider
{
    private static readonly Guid BatteryLevelGuid = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
    private const uint BatteryLevelPid = 2;

    private static readonly Guid FriendlyNameGuid = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private const uint FriendlyNamePid = 14;

    private const string HandsFreeServiceMarker = "{0000111E-0000-1000-8000-00805F9B34FB}";
    private const string DeviceEnumeratorName = "BTHENUM";
    private const uint FilterEnumerator = 0x1;

    private const uint DevPropTypeByte = 0x3;
    private const uint DevPropTypeString = 0x12;

    public string ProviderName => "Bluetooth (HFP)";

    public Task<IReadOnlyList<GearObservation>> DiscoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<GearObservation> observations = Discover(cancellationToken);
        return Task.FromResult(observations);
    }

    private static List<GearObservation> Discover(CancellationToken cancellationToken)
    {
        var result = new List<GearObservation>();
        var instances = EnumerateInstances();

        foreach (var instanceId in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Battery over HFP is published on the "Hands-Free AG" service instance of the device.
            if (!instanceId.Contains(HandsFreeServiceMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var percent = TryReadBatteryLevel(instanceId);
                if (percent is null)
                {
                    continue;
                }

                var address = ExtractAddress(instanceId);
                var name = address is null ? null : FindDeviceName(instances, address);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                result.Add(new GearObservation
                {
                    DeviceId = $"bthfp:{address}",
                    Name = name,
                    Source = "Bluetooth (HFP)",
                    Kind = GearKindClassifier.Classify(name),
                    IsConnected = true,
                    IsTrusted = true,
                    Battery = new BatteryReading { Percent = percent },
                });
            }
            catch
            {
                // A single broken device must not break the whole scan.
            }
        }

        return result;
    }

    /// <summary>All existing device instance IDs from the BTHENUM (classic Bluetooth) enumerator.</summary>
    private static List<string> EnumerateInstances()
    {
        var result = new List<string>();

        try
        {
            if (CM_Get_Device_ID_List_SizeW(out var length, DeviceEnumeratorName, FilterEnumerator) != 0 || length == 0)
            {
                return result;
            }

            var buffer = new char[length];
            if (CM_Get_Device_ID_ListW(DeviceEnumeratorName, buffer, length, FilterEnumerator) != 0)
            {
                return result;
            }

            result.AddRange(new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries));
        }
        catch
        {
            // Enumeration failures must not break the scan.
        }

        return result;
    }

    /// <summary>Bluetooth address of the device, taken from the service instance id (the MAC in the tail).</summary>
    private static string? ExtractAddress(string instanceId)
    {
        var separator = instanceId.LastIndexOf('\\');
        if (separator < 0 || separator + 1 >= instanceId.Length)
        {
            return null;
        }

        var tail = instanceId[(separator + 1)..];
        var underscore = tail.IndexOf('_');
        var before = underscore < 0 ? tail : tail[..underscore];
        var ampersand = before.LastIndexOf('&');
        var address = ampersand < 0 ? before : before[(ampersand + 1)..];

        return address.Length == 12 && address.All(Uri.IsHexDigit) ? address.ToUpperInvariant() : null;
    }

    /// <summary>Product name of the device, taken from its base "DEV_" instance (no localized suffixes).</summary>
    private static string? FindDeviceName(List<string> instances, string address)
    {
        var marker = "BTHENUM\\DEV_" + address;

        foreach (var instanceId in instances)
        {
            if (!instanceId.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return ReadStringProperty(instanceId, FriendlyNameGuid, FriendlyNamePid);
        }

        return null;
    }

    private static int? TryReadBatteryLevel(string instanceId)
    {
        if (CM_Locate_DevNodeW(out var device, instanceId, 0) != 0)
        {
            return null;
        }

        var key = new DevPropKey(BatteryLevelGuid, BatteryLevelPid);
        var buffer = new byte[1];
        var size = (uint)buffer.Length;

        if (CM_Get_DevNode_PropertyW(device, ref key, out var type, buffer, ref size, 0) != 0)
        {
            return null;
        }

        if (type != DevPropTypeByte || size < 1)
        {
            return null;
        }

        return buffer[0] <= 100 ? buffer[0] : null;
    }

    private static string? ReadStringProperty(string instanceId, Guid formatId, uint propertyId)
    {
        if (CM_Locate_DevNodeW(out var device, instanceId, 0) != 0)
        {
            return null;
        }

        var key = new DevPropKey(formatId, propertyId);
        var size = 0u;

        // The first probe intentionally fails with CR_BUFFER_SMALL, which reports the required size.
        const uint CrBufferSmall = 0x1A;
        var status = CM_Get_DevNode_PropertyW(device, ref key, out var type, null, ref size, 0);
        if (status != 0 && status != CrBufferSmall)
        {
            return null;
        }

        if (size == 0 || type != DevPropTypeString)
        {
            return null;
        }

        var buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(device, ref key, out type, buffer, ref size, 0) != 0)
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public DevPropKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string filter, char[] buffer, uint bufferLength, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint deviceInstance, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_PropertyW(
        uint deviceInstance,
        ref DevPropKey propertyKey,
        out uint propertyType,
        byte[]? propertyBuffer,
        ref uint propertyBufferSize,
        uint flags);
}
