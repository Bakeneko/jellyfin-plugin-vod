using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class CookieJarTests
    {
        [Fact]
        public async Task AcquireAsync_TakesNoLock_WhenNoCookiesAreConfigured()
        {
            // No Plugin.Instance in a unit test, so this is the "nothing configured" path: it must
            // hand out a lease with no path and no lock, and it must do so for any number of
            // concurrent callers of the same lane.
            using var jar = new CookieJar(NullLogger<CookieJar>.Instance);

            using var first = await jar.AcquireAsync("youtube", CancellationToken.None);
            using var second = await jar.AcquireAsync("youtube", CancellationToken.None);

            Assert.Null(first.Path);
            Assert.Null(second.Path);
        }

        [Fact]
        public void WorkingFileNameFor_IsStablePerLane_AndSafeAsAFileName()
        {
            Assert.Equal("cookies-youtube.txt", CookieJar.WorkingFileNameFor("youtube"));
            Assert.Equal("cookies-arte.txt", CookieJar.WorkingFileNameFor("arte"));
            Assert.NotEqual(
                CookieJar.WorkingFileNameFor("youtube"),
                CookieJar.WorkingFileNameFor("arte"));

            // An unknown platform's lane is its host, which can hold characters a file name cannot.
            var name = CookieJar.WorkingFileNameFor("odd/host:name");
            Assert.DoesNotContain(Path.GetInvalidFileNameChars(), name.Contains);
        }
    }
}
