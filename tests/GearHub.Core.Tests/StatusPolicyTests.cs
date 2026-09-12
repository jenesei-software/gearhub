using GearHub.Core.Models;
using GearHub.Core.Services;
using Xunit;

namespace GearHub.Core.Tests;

public class StatusPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly StatusPolicy _policy = new();

    [Fact]
    public void Connected_IsOnline()
        => Assert.Equal(GearStatus.Online, _policy.Evaluate(true, false, Now, Now));

    [Fact]
    public void ConnectedWithFault_IsAttention()
        => Assert.Equal(GearStatus.Attention, _policy.Evaluate(true, true, Now, Now));

    [Fact]
    public void RecentlyOffline_IsAttention()
        => Assert.Equal(GearStatus.Attention, _policy.Evaluate(false, false, Now - TimeSpan.FromHours(2), Now));

    [Fact]
    public void LongOffline_IsLost()
        => Assert.Equal(GearStatus.Lost, _policy.Evaluate(false, false, Now - TimeSpan.FromDays(3), Now));

    [Fact]
    public void WindowBoundary_IsAttention()
        => Assert.Equal(GearStatus.Attention, _policy.Evaluate(false, false, Now - TimeSpan.FromHours(6), Now));

    [Fact]
    public void JustOverWindow_IsLost()
        => Assert.Equal(GearStatus.Lost, _policy.Evaluate(false, false, Now - TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1), Now));
}
