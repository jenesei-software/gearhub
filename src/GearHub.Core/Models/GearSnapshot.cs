namespace GearHub.Core.Models;

/// <summary>Результат одного цикла сканирования.</summary>
public sealed record GearSnapshot(IReadOnlyList<GearDevice> Devices, IReadOnlyList<string> ProviderErrors)
{
    public static GearSnapshot Empty { get; } = new([], []);
}
