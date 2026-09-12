using GearHub.Core.Models;
using GearHub.Core.Services;
using Xunit;

namespace GearHub.Core.Tests;

public class GearKindClassifierTests
{
    [Theory]
    [InlineData("MX Master 3S", GearKind.Mouse)]
    [InlineData("MX Anywhere 3S", GearKind.Mouse)]
    [InlineData("Logitech G502 HERO", GearKind.Mouse)]
    [InlineData("PRO X SUPERLIGHT", GearKind.Mouse)]
    [InlineData("MX Keys S", GearKind.Keyboard)]
    [InlineData("MX Mechanical Mini", GearKind.Keyboard)]
    [InlineData("K380 Multi-Device Keyboard", GearKind.Keyboard)]
    [InlineData("G915 X LIGHTSPEED", GearKind.Keyboard)]
    [InlineData("G435 Wireless Gaming Headset", GearKind.Headset)]
    [InlineData("Xbox Wireless Controller", GearKind.Gamepad)]
    public void Classify_RecognizesCommonNames(string name, GearKind expected)
        => Assert.Equal(expected, GearKindClassifier.Classify(name));

    [Fact]
    public void Classify_UnknownName_ReturnsOther()
        => Assert.Equal(GearKind.Other, GearKindClassifier.Classify("USB Receiver"));

    [Fact]
    public void Classify_EmptyName_ReturnsOther()
        => Assert.Equal(GearKind.Other, GearKindClassifier.Classify(null));
}
