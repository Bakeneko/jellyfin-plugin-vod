using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Localization
{
    /// <summary>
    /// Resolves plugin strings from embedded JSON resources under <c>Strings/</c>.
    /// Language selection: server UICulture → language prefix → "en" fallback → key itself.
    /// Resource naming convention: <c>Jellyfin.Plugin.VOD.Strings.{lang}.json</c>
    /// where {lang} is a BCP-47 primary subtag (e.g. "en", "fr", "de").
    /// </summary>
    public sealed partial class VodLocalizer : IVodLocalizer
    {
        private readonly IServerConfigurationManager _config;
        private readonly ILogger<VodLocalizer> _logger;
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _strings;

        /// <summary>Initializes a new instance of the <see cref="VodLocalizer"/> class.</summary>
        public VodLocalizer(IServerConfigurationManager config, ILogger<VodLocalizer> logger)
        {
            _config = config;
            _logger = logger;
            _strings = LoadEmbeddedStrings();
        }

        /// <inheritdoc />
        public string Get(string key)
        {
            var culture = _config.Configuration.UICulture ?? "en";
            // try current language first, then default to english
            var lang = culture.Split('-')[0];

            if (_strings.TryGetValue(lang, out var table) && table.TryGetValue(key, out var value))
                return value;

            if (_strings.TryGetValue("en", out var enTable) && enTable.TryGetValue(key, out var enValue))
                return enValue;

            return key;
        }

        private Dictionary<string, IReadOnlyDictionary<string, string>> LoadEmbeddedStrings()
        {
            var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(
                StringComparer.OrdinalIgnoreCase);

            var asm = GetType().Assembly;
            foreach (var resourceName in asm.GetManifestResourceNames()
                .Where(n => n.Contains(".Strings.", StringComparison.Ordinal)
                         && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                // "Jellyfin.Plugin.VOD.Strings.en.json" → lang = "en"
                var parts = resourceName.Split('.');
                if (parts.Length < 2) continue;
                var lang = parts[^2];

                try
                {
                    using var stream = asm.GetManifestResourceStream(resourceName)!;
                    using var reader = new StreamReader(stream);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd())
                               ?? [];
                    result[lang] = dict;
                }
                catch (Exception ex)
                {
                    LogResourceLoadFailed(resourceName, ex);
                }
            }

            return result;
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
            Message = "Failed to load string resource {Resource}")]
        private partial void LogResourceLoadFailed(string resource, Exception exception);
    }
}
