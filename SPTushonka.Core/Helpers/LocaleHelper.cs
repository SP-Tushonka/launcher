using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SPTarkov.Core.Configuration;
using SPTarkov.Core.SPT;

namespace SPTarkov.Core.Helpers;

/// <summary>
/// This will only be used for front facing UI elements, Logging will all be EN
/// </summary>
public class LocaleHelper
{
    private const string FallbackLocaleTag = "en";
    private const string LanguagesFileName = "languages.json";

    // We renamed locale codes to match the server, handle previous launcher legacy tags here
    private static readonly Dictionary<string, string> LegacyTags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["es"] = "es-es",
        ["zh-hans"] = "zh-cn",
        ["zh-hant"] = "zh-TW",
    };

    private readonly Lock _lock = new();
    private readonly ILogger<LocaleHelper> _logger;
    private readonly ConfigHelper _configHelper;
    private readonly Dictionary<string, Dictionary<string, string>> _locales = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _languageNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedKeys = [];
    private Dictionary<string, string> _fallbackLocale = new();
    private Dictionary<string, string> _selectedLocale = new();
    private string _selectedTag = FallbackLocaleTag;
    private bool _logLocalesOne;

    public LocaleHelper(ILogger<LocaleHelper> logger, ConfigHelper configHelper)
    {
        _logger = logger;
        _configHelper = configHelper;
        var configuredLocale = _configHelper.GetConfig().Language;
        _logger.LogDebug("Configured locale: {locale}", configuredLocale);

        lock (_lock)
        {
            _logger.LogDebug("Loading locales from {dirPath}", Paths.LocalesPath);

            if (!Directory.Exists(Paths.LocalesPath))
            {
                _logger.LogCritical("Directory {dirPath} does not exist", Paths.LocalesPath);
                throw new Exception("Directory does not exist");
            }

            foreach (var file in Directory.GetFiles(Paths.LocalesPath, "*.json"))
            {
                if (string.Equals(Path.GetFileName(file), LanguagesFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var tag = Path.GetFileNameWithoutExtension(file);
                if (!IsLocaleTag(tag))
                {
                    _logger.LogWarning(
                        "\"{file}\" is not named after a locale and was skipped, it is likely left over from an older install",
                        file
                    );
                    continue;
                }

                var localeDict = Read(file);
                if (localeDict is null)
                {
                    continue;
                }

                _locales[tag] = localeDict;
            }

            var languages = Read(Path.Join(Paths.LocalesPath, LanguagesFileName));
            if (languages is null)
            {
                _logger.LogError("{file} could not be read, the language picker will show locale codes", LanguagesFileName);
            }
            else
            {
                foreach (var (tag, name) in languages)
                {
                    _languageNames[tag] = name;
                }
            }

            if (!_locales.TryGetValue(FallbackLocaleTag, out _fallbackLocale!))
            {
                _logger.LogCritical("Fallback locale \"{locale}\" was not loaded", FallbackLocaleTag);
                _fallbackLocale = new Dictionary<string, string>();
            }

            ApplyLocale(configuredLocale);

            if (LegacyTags.ContainsKey(configuredLocale))
            {
                _configHelper.SetLocale(_selectedTag);
            }
        }
    }

    public string Get(string key)
    {
        if (_selectedLocale.TryGetValue(key, out var value))
        {
            return value;
        }

        if (_fallbackLocale.TryGetValue(key, out var fallbackValue))
        {
            ReportOnce(
                key,
                () =>
                    _logger.LogWarning(
                        "Key \"{key}\" not found in locale \"{locale}\", falling back to \"{fallback}\"",
                        key,
                        _selectedTag,
                        FallbackLocaleTag
                    )
            );
            return fallbackValue;
        }

        ReportOnce(key, () => _logger.LogError("Key \"{key}\" not found in locale \"{locale}\" or the fallback locale", key, _selectedTag));
        return "Value was not found in locale, Please report this to SPT for fixing";
    }

    public Dictionary<string, string> GetAvailableLocales()
    {
        if (!_logLocalesOne)
        {
            _logger.LogDebug("Available locales: {locales}", _locales.Keys);
            _logLocalesOne = true;
        }

        return _locales.Keys.ToDictionary(tag => tag, tag => _languageNames.GetValueOrDefault(tag, tag));
    }

    public void SetLocale(string locale)
    {
        ApplyLocale(locale);
        _configHelper.SetLocale(_selectedTag);
    }

    private void ApplyLocale(string locale)
    {
        var tag = LegacyTags.GetValueOrDefault(locale, locale);

        if (!_locales.TryGetValue(tag, out var resolved))
        {
            _logger.LogWarning("Locale \"{locale}\" is not available, falling back to \"{fallback}\"", locale, FallbackLocaleTag);
            tag = FallbackLocaleTag;
            resolved = _fallbackLocale;
        }

        _selectedTag = tag;
        _selectedLocale = resolved;

        lock (_lock)
        {
            _reportedKeys.Clear();
        }
    }

    /// <summary>
    /// Locale files are named after their tag, anything else is a leftover from before that rename.
    /// </summary>
    private static bool IsLocaleTag(string tag)
    {
        try
        {
            CultureInfo.GetCultureInfo(tag);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private Dictionary<string, string>? Read(string file)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
            if (parsed is null)
            {
                _logger.LogError("Locale file \"{file}\" is empty and was skipped", file);
            }

            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Locale file \"{file}\" could not be read and was skipped", file);
            return null;
        }
    }

    /// <summary>
    /// A missing key is hit on every render of the component that uses it, log it once per locale instead.
    /// </summary>
    private void ReportOnce(string key, Action log)
    {
        lock (_lock)
        {
            if (!_reportedKeys.Add(key))
            {
                return;
            }
        }

        log();
    }
}
