using System;
using System.Collections.Generic;
using Jellyfin.Plugin.VOD.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class StoredStreamsTests
    {
        [Fact]
        public void KeyFor_NamesThePrimaryAndEachTier()
        {
            Assert.Equal(StoredStreams.PrimaryKey, StoredStreams.KeyFor(null, null));
            Assert.Equal("1080/h264", StoredStreams.KeyFor(1080, "h264"));
            Assert.Equal(StoredStreams.PrimaryKey, StoredStreams.KeyFor(1080, null));
        }

        [Fact]
        public void Serialize_RoundTripsWhatSaveMediaStreamsNeeds()
        {
            var json = StoredStreams.Serialize(new Dictionary<string, IReadOnlyList<MediaStream>>
            {
                ["1080/h264"] =
                [
                    new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", Height = 1080, BitRate = 3000000 },
                    new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", Title = "français", Language = "fr" },
                ],
            });

            var back = StoredStreams.Deserialize(json)!;

            var streams = back["1080/h264"];
            Assert.Equal(MediaStreamType.Video, streams[0].Type);
            Assert.Equal(1080, streams[0].Height);
            Assert.Equal(3000000, streams[0].BitRate);
            Assert.Equal("français", streams[1].Title);
            Assert.Equal(1, streams[1].Index);
        }

        [Fact]
        public void Serialize_StoresNeitherComputedNorDefaultMembers_AndKeepsTextLiteral()
        {
            var json = StoredStreams.Serialize(new Dictionary<string, IReadOnlyList<MediaStream>>
            {
                [StoredStreams.PrimaryKey] =
                [
                    new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", Title = "français", Language = "fr" },
                ],
            });

            Assert.DoesNotContain("\"DisplayTitle\"", json, StringComparison.Ordinal);
            Assert.DoesNotMatch("\":(false|0)[,}]", json);
            Assert.Contains("\"français\"", json, StringComparison.Ordinal);
        }

        [Fact]
        public void Serialize_RoundTripsADefaultIndexAndATrueFlag()
        {
            var json = StoredStreams.Serialize(new Dictionary<string, IReadOnlyList<MediaStream>>
            {
                [StoredStreams.PrimaryKey] =
                [
                    new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", IsDefault = true },
                ],
            });

            var stream = StoredStreams.Deserialize(json)![StoredStreams.PrimaryKey][0];

            Assert.Equal(0, stream.Index);
            Assert.True(stream.IsDefault);
            Assert.Equal(MediaStreamType.Video, stream.Type);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        public void Deserialize_AnswersNull_ForNothingOrGarbage(string? json)
            => Assert.Null(StoredStreams.Deserialize(json));

        [Fact]
        public void Clone_ReturnsIndependentInstances()
        {
            var original = new List<MediaStream> { new() { Type = MediaStreamType.Audio, Title = "a" } };

            var copy = StoredStreams.Clone(original);
            copy[0].Title = "b";

            Assert.Equal("a", original[0].Title);
        }
    }
}
