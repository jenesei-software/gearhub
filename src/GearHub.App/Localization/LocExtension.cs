using System.Windows.Markup;
using GearHub.Core.Localization;

namespace GearHub.App.Localization;

/// <summary>String localization in XAML: {loc:Loc Key}.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
