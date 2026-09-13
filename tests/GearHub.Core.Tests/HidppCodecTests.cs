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
    public void BuildLongRequest_HasExpectedLayout()
    {
        var request = HidppCodec.BuildLongRequest(deviceIndex: 1, featureIndex: 0x00, functionId: 0x00, HidppCodec.SolaarSoftwareId, 0x10, 0x04, 0x00);

        Assert.Equal(20, request.Length);
        Assert.Equal(0x11, request[0]);
        Assert.Equal(0x01, request[1]);
        Assert.Equal(0x00, request[2]);
        Assert.Equal(0x0B, request[3]);
        Assert.Equal(0x10, request[4]);
        Assert.Equal(0x04, request[5]);
        Assert.Equal(0x00, request[6]);
        Assert.Equal(0x00, request[19]);
    }

    [Fact]
    public void TryParseDeviceReply_ParsesFeatureReply()
    {
        // getFeature(0x1004) reply from MX Master: feature index 0x08 on the device.
        var report = new byte[20];
        report[0] = 0x11;
        report[1] = 0x01;
        report[2] = 0x00;
        report[3] = 0x0B;
        report[4] = 0x08;
        report[5] = 0x00;
        report[6] = 0x03;

        Assert.True(HidppCodec.TryParseDeviceReply(report, out var reply));
        Assert.False(reply.IsError);
        Assert.Equal(0x01, reply.DeviceIndex);
        Assert.Equal(0x00, reply.FeatureIndex);
        Assert.Equal(0x0B, reply.FunctionByte);
        Assert.Equal(0x08, reply.Payload[0]);
    }

    [Fact]
    public void TryParseDeviceReply_ParsesErrorReply()
    {
        // Error frame: [0x11, devIdx, 0xFF, feature, func_sw, error].
        byte[] report = [0x11, 0x01, 0xFF, 0x81, 0x0D, 0x06, 0x00];

        Assert.True(HidppCodec.TryParseDeviceReply(report, out var reply));
        Assert.True(reply.IsError);
        Assert.Equal(0x81, reply.FeatureIndex);
        Assert.Equal(0x0D, reply.FunctionByte);
        Assert.Equal(0x06, reply.ErrorCode);
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
