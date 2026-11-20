namespace TwitchDownloaderWeb.Data
{
    /// <summary>When a status is given to an item by itself (<see cref="TeamStatus.SetWhen"/>). Never moves an item back to an earlier status.</summary>
    public enum StatusTrigger
    {
        /// <summary>Only from the menu.</summary>
        ByHand,
        /// <summary>A clip or video is made (copies too).</summary>
        NewItem,
        /// <summary>A Player 2 (horizontal) edit of the clip or video is made.</summary>
        EditMade,
        /// <summary>A vertical edit of the clip or video is made.</summary>
        VerticalEditMade,
        /// <summary>The clip or video is sent to Export.</summary>
        Exported,
    }

    /// <summary>
    /// A step of the team's own workflow for clips and videos (badrfly 2026-11-20, "Redesign the library for large teams", step 4): the team
    /// owner, or a role with <see cref="TeamPermission.ManageStatuses"/>, makes them on the Team page (name, color, order, when it's set by itself,
    /// hidden from lists). New teams start with To do / Editing / Ready / Posted (<see cref="Defaults"/>). An item has at most one.
    /// </summary>
    public class TeamStatus
    {
        public const int MaxNameLength = 40;
        public const int MaxPerTeam = 12;

        public int Id { get; set; }
        public int TeamId { get; set; }
        public required string Name { get; set; }

        /// <summary>One of <see cref="VodMarker.Colors"/>.</summary>
        public string Color { get; set; } = "gray";

        /// <summary>Order on the page (0 first). Statuses set by themselves only move items to a later one.</summary>
        public int Position { get; set; }

        /// <summary>At most one status of a team has each trigger other than <see cref="StatusTrigger.ByHand"/>.</summary>
        public StatusTrigger SetWhen { get; set; }

        /// <summary>Items with it are left out of the CLIPS and VIDEOS lists unless that status is picked (e.g. Posted).</summary>
        public bool HideFromLists { get; set; }

        public DateTime CreatedAtUtc { get; set; }

        public static readonly IReadOnlyList<(string Name, string Color, StatusTrigger SetWhen)> Defaults =
        [
            ("To do", "gray", StatusTrigger.NewItem),
            ("Editing", "yellow", StatusTrigger.EditMade),
            ("Ready", "violet", StatusTrigger.Exported),
            ("Posted", "lime", StatusTrigger.ByHand),
        ];
    }

    /// <summary>A team's tag (lower case, no '#'), made the first time a member puts it on a clip or video.</summary>
    public class TeamTag
    {
        public const int MaxNameLength = 30;
        public const int MaxPerItem = 10;

        public int Id { get; set; }
        public int TeamId { get; set; }
        public required string Name { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    public class EntryTag
    {
        public int EntryId { get; set; }
        public int TagId { get; set; }
        public TeamTag? Tag { get; set; }
    }

    public class VideoTag
    {
        public int VideoId { get; set; }
        public int TagId { get; set; }
        public TeamTag? Tag { get; set; }
    }
}
