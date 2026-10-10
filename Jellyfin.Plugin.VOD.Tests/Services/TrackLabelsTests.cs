using System.Collections.Generic;
using Jellyfin.Plugin.VOD.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class TrackLabelsTests
    {
        [Fact]
        public void Apply_PairsWithinATypeByPosition_EvenWhenTheVideoComesLast()
        {
            // The order Jellyfin's probe returns on our route: audio, subtitles, video last
            // (spec 2026-10-08-tier-stream-description-design.md, §8.1 A).
            var described = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Audio, Index = 0 },
                new() { Type = MediaStreamType.Audio, Index = 1 },
                new() { Type = MediaStreamType.Subtitle, Index = 2 },
                new() { Type = MediaStreamType.Video, Index = 3 }
            };

            var fromMaster = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Audio, Title = "français" },
                new() { Type = MediaStreamType.Audio, Title = "français (confort audio)" },
                new() { Type = MediaStreamType.Subtitle, Title = "français" },
                new() { Type = MediaStreamType.Video, Title = null }
            };

            var changed = TrackLabels.Apply(described, fromMaster);

            Assert.Equal(3, changed);
            Assert.Equal("français", described[0].Title);
            Assert.Equal("français (confort audio)", described[1].Title);
            Assert.Equal("français", described[2].Title);
            Assert.Null(described[3].Title);
        }

        [Fact]
        public void Apply_APlaceholderLabel_IsNotStored()
        {
            var described = new List<MediaStream> { new() { Type = MediaStreamType.Audio } };
            var fromMaster = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Audio, Title = "Default" }
            };

            Assert.Equal(0, TrackLabels.Apply(described, fromMaster));
            Assert.Null(described[0].Title);
        }
    }
}
