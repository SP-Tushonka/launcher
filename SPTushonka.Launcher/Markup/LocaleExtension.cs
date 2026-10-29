using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Core.Helpers;

namespace SPTarkov.Launcher.Markup;

/// <summary>
///     Markup extension that looks up a launcher locale string. Set <c>Upper</c> for the uppercase button and label style.
/// </summary>
public class LocaleExtension(string key)
{
    public string Key { get; } = key;

    public bool Upper { get; set; }

    public object ProvideValue(IServiceProvider serviceProvider)
    {
        var text = App.Services.GetRequiredService<LocaleHelper>().Get(Key);
        return Upper ? text.ToUpperInvariant() : text;
    }
}
