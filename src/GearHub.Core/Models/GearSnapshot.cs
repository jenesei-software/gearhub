namespace GearHub.Core.Models;

/// <summary>Result of a single scan cycle.</summary>
public sealed record GearSnapshot(IReadOnlyList<GearDevice> Devices, IReadOnlyList<string> ProviderErrors)
{
    public static GearSnapshot Empty { get; } = new([], []);
}
