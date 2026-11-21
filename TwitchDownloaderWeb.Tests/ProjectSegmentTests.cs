using System.Text.Json;
using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Endpoints;

namespace TwitchDownloaderWeb.Tests
{
    /// <summary>An edit made only from one segment names that segment (the library lists it under the segment).</summary>
    public sealed class ProjectSegmentTests
    {
        private static EditProject Project(int? entryId, params ProjectSource?[] sources) => new()
        {
            EntryId = entryId,
            Content = JsonSerializer.Serialize(new ProjectContent(
                [new ProjectTrack("t1", null, false, false, false)],
                sources.Select((s, i) => new ProjectPiece($"p{i}", "t1", i * 10, 1, 100 + i * 10, 105 + i * 10, s)).ToList()),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };

        [Fact]
        public void OnlyEditsOfOneSegmentNameIt()
        {
            Assert.Equal(7, ProjectEndpoints.SegmentOf(Project(null, new ProjectSource("segment", 7), new ProjectSource("segment", 7))));
            Assert.Null(ProjectEndpoints.SegmentOf(Project(null, new ProjectSource("segment", 7), new ProjectSource("segment", 8))));
            Assert.Null(ProjectEndpoints.SegmentOf(Project(null, new ProjectSource("segment", 7), new ProjectSource("vod", 1))));
            Assert.Null(ProjectEndpoints.SegmentOf(Project(null, (ProjectSource?)null)));
            Assert.Null(ProjectEndpoints.SegmentOf(Project(null)));
            // A clip's edit belongs to the clip
            Assert.Null(ProjectEndpoints.SegmentOf(Project(3, new ProjectSource("segment", 7))));
        }
    }
}
