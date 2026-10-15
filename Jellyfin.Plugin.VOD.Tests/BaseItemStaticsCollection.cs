using Xunit;

namespace Jellyfin.Plugin.VOD.Tests
{
    /// <summary>
    /// Serializes the test classes that install <c>BaseItem.LibraryManager</c>: it is a process-wide
    /// static, and xUnit runs test classes in parallel, so two classes writing it would race.
    /// </summary>
    [CollectionDefinition(Name)]
    public sealed class BaseItemStaticsCollection
    {
        /// <summary>The collection's name.</summary>
        public const string Name = "BaseItem statics";
    }
}
