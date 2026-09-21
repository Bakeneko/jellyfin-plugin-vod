namespace Jellyfin.Plugin.VOD.Localization
{
    /// <summary>Resolves plugin-specific localised strings.</summary>
    public interface IVodLocalizer
    {
        /// <summary>
        /// Returns the string for <paramref name="key"/> in the server's configured UI language,
        /// or the English fallback, or <paramref name="key"/> itself if nothing matches.
        /// </summary>
        string Get(string key);
    }
}
