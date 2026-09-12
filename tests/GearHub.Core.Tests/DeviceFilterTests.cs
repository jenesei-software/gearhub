using GearHub.Core.Models;
using GearHub.Core.Services;
using Xunit;

namespace GearHub.Core.Tests;

public class DeviceFilterTests
{
    private static readonly IReadOnlySet<string> NoIgnores = new HashSet<string>(StringComparer.Ordinal);

    private readonly DeviceFilter _filter = new();

    [Theory]
    [InlineData("Bluetooth LE Generic Attribute Service")]
    [InlineData("HID-compliant device")]
    [InlineData("USB Input Device")]
    [InlineData("Generic Access Profile")]
    [InlineData("Microsoft Bluetooth LE Enumerator")]
    public void HidesJunkNames(string name)
    {
        var observation = new GearObservation { DeviceId = "test:1", Name = name, Source = "test" };

        Assert.True(_filter.ShouldHide(observation, NoIgnores));
    }

    [Fact]
    public void KeepsKnownDeviceWithBattery()
    {
        var observation = new GearObservation
        {
            DeviceId = "ble:1",
            Name = "MX Master 3S",
            Source = "Bluetooth LE",
            Kind = GearKind.Mouse,
            Battery = new BatteryReading { Percent = 80 },
        };

        Assert.False(_filter.ShouldHide(observation, NoIgnores));
    }

    [Fact]
    public void KeepsDeviceWithoutBatteryButKnownKind()
    {
        var observation = new GearObservation
        {
            DeviceId = "ble:2",
            Name = "WH-1000XM5",
            Source = "Bluetooth LE",
            Kind = GearKind.Headset,
        };

        Assert.False(_filter.ShouldHide(observation, NoIgnores));
    }

    [Fact]
    public void HidesUnknownDeviceWithoutBattery()
    {
        var observation = new GearObservation
        {
            DeviceId = "ble:3",
            Name = "Unknown gadget",
            Source = "Bluetooth LE",
        };

        Assert.True(_filter.ShouldHide(observation, NoIgnores));
    }

    [Fact]
    public void KeepsUnknownDeviceWithFault()
    {
        var observation = new GearObservation
        {
            DeviceId = "ble:4",
            Name = "Strange device",
            Source = "Bluetooth LE",
            HasFault = true,
        };

        Assert.False(_filter.ShouldHide(observation, NoIgnores));
    }

    [Fact]
    public void HidesIgnoredDevice()
    {
        IReadOnlySet<string> ignored = new HashSet<string>(StringComparer.Ordinal) { "ble:1" };

        var observation = new GearObservation
        {
            DeviceId = "ble:1",
            Name = "MX Master 3S",
            Source = "Bluetooth LE",
            Kind = GearKind.Mouse,
            Battery = new BatteryReading { Percent = 80 },
        };

        Assert.True(_filter.ShouldHide(observation, ignored));
    }

    [Fact]
    public void HidesDeviceWithoutName()
    {
        var observation = new GearObservation { DeviceId = "ble:5", Name = "  ", Source = "Bluetooth LE" };

        Assert.True(_filter.ShouldHide(observation, NoIgnores));
    }

    [Fact]
    public void HidesLogitechVirtualKeyboard()
    {
        var observation = new GearObservation
        {
            DeviceId = "hid:1",
            Name = "Logitech G HUB Virtual Keyboard",
            Source = "HID",
        };

        Assert.True(_filter.ShouldHide(observation, NoIgnores));
    }

    [Fact]
    public void KeepsTrustedDeviceWithoutBattery()
    {
        var observation = new GearObservation
        {
            DeviceId = "hidpp:path#1",
            Name = "Logitech-устройство 1",
            Source = "Logitech HID++",
            IsTrusted = true,
        };

        Assert.False(_filter.ShouldHide(observation, NoIgnores));
    }
}
