using GearHub.Core.Models;
using GearHub.Providers.Windows.Logitech;
using Xunit;

namespace GearHub.Core.Tests;

public class HidppCodecTests
{
    [Fact]
    public void BuildShortRequest_HasExpectedLayout()
    {
        var request = HidppCodec.BuildShortRequest(deviceIndex: 2, featureIndex: 0x05, functionId: 0x01, softwareId: 0x0A, parameter0: 0xAB);

        Assert.Equal(7, request.Length);
        Assert.Equal(0x10, request[0]);
        Assert.Equal(2, request[1]);
        Assert.Equal(0x05, request[2]);
        Assert.Equal(0x1A, request[3]);
        Assert.Equal(0xAB, request[4]);
    }

    [Fact]
    public void TryParseResponse_ParsesShortResponse()
    {
        byte[] report = [0x10, 0x01, 0x04, 0x0A, 0x57, 0x03, 0x00];

        Assert.True(HidppCodec.TryParseResponse(report, out var response));
        Assert.False(response.IsError);
        Assert.Equal(0x01, response.DeviceIndex);
        Assert.Equal(0x04, response.FeatureIndex);
        Assert.Equal(0x00, response.FunctionId);
        Assert.Equal(0x0A, response.SoftwareId);
        Assert.Equal(0x57, response.Parameters[0]);
    }

    [Fact]
    public void TryParseResponse_ParsesLongResponse()
    {
        var report = new byte[20];
        report[0] = 0x11;
        report[1] = 0x02;
        report[2] = 0x05;
        report[3] = 0x1A;
        report[4] = (byte)'M';
        report[5] = (byte)'X';

        Assert.True(HidppCodec.TryParseResponse(report, out var response));
        Assert.Equal(16, response.Parameters.Length);
        Assert.Equal((byte)'M', response.Parameters[0]);
        Assert.Equal((byte)'X', response.Parameters[1]);
    }

    [Fact]
    public void TryParseResponse_ParsesError()
    {
        byte[] report = [0x10, 0x01, 0xFF, 0x0A, 0x09, 0x00, 0x00];

        Assert.True(HidppCodec.TryParseResponse(report, out var response));
        Assert.True(response.IsError);
        Assert.Equal(0x09, response.ErrorCode);
    }

    [Fact]
    public void TryParseResponse_RejectsForeignReport()
    {
        byte[] report = [0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

        Assert.False(HidppCodec.TryParseResponse(report, out _));
    }

    [Theory]
    [InlineData(4200, 100)]
    [InlineData(4100, 100)]
    [InlineData(4000, 90)]
    [InlineData(3750, 50)]
    [InlineData(3400, 5)]
    [InlineData(3000, 0)]
    public void EstimatePercentFromMillivolts_FollowsCurve(int millivolts, int expectedPercent)
        => Assert.Equal(expectedPercent, HidppCodec.EstimatePercentFromMillivolts(millivolts));

    [Theory]
    [InlineData(0, CoarseBatteryLevel.Empty)]
    [InlineData(1, CoarseBatteryLevel.Low)]
    [InlineData(2, CoarseBatteryLevel.High)]
    [InlineData(3, CoarseBatteryLevel.Full)]
    [InlineData(9, CoarseBatteryLevel.Unknown)]
    public void MapLevel_MapsUnifiedBatteryLevel(byte level, CoarseBatteryLevel expected)
        => Assert.Equal(expected, HidppCodec.MapLevel(level));
}
