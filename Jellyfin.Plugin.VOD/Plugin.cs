using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.VOD.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.VOD
{
    /// <summary>
    /// Plugin entrypoint.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// </summary>
        /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
        /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        /// <inheritdoc />
        public override string Name => "VOD";

        /// <inheritdoc />
        public override string Description => "Get a virtual library of VOD channel/playlist with videos synched, cached and streamed on demand via yt-dlp.";

        /// <inheritdoc />
        public override Guid Id => Guid.Parse("16bb0693-4248-478b-a1e5-a4508326245c");

        /// <summary>
        /// Gets the current plugin instance.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <summary>
        /// Returns the plugin's data folder, creating it when it does not exist.
        /// </summary>
        /// <remarks>
        /// Jellyfin assigns <see cref="BasePlugin.DataFolderPath"/> but never creates the directory,
        /// so on a fresh install nothing is on disk and the first write fails. Every write into the
        /// data folder must therefore go through here rather than reading
        /// <see cref="BasePlugin.DataFolderPath"/> directly. Reading the property to compose a path
        /// stays fine — only writes need the guarantee. The call is a cheap syscall on an existing
        /// directory, and repeating it per write also recovers a folder removed while the server runs.
        /// </remarks>
        /// <returns>The absolute path to the plugin's data folder.</returns>
        public string EnsureDataFolder()
        {
            Directory.CreateDirectory(DataFolderPath);
            return DataFolderPath;
        }

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
                }
            };
        }
    }
}
