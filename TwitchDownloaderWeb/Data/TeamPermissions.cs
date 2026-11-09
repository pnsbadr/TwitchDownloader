namespace TwitchDownloaderWeb.Data
{
    /// <summary>
    /// What a team role allows. The team owner has all of them. Stored as a bit mask on <see cref="TeamRole.Permissions"/>, so
    /// never renumber a value; add new ones at the end.
    /// </summary>
    [Flags]
    public enum TeamPermission : long
    {
        None = 0,

        /// <summary>See the library, saved channels and VOD browser; watch VODs; play and download files and vertical renders.</summary>
        ViewLibrary = 1L << 0,
        AddEntries = 1L << 1,
        /// <summary>Rename and re-cut entries they added.</summary>
        EditOwnEntries = 1L << 2,
        EditAnyEntries = 1L << 3,
        DeleteOwnEntries = 1L << 4,
        DeleteAnyEntries = 1L << 5,
        /// <summary>Make on-demand files for reference-only entries (and retry failed downloads).</summary>
        GenerateFiles = 1L << 6,
        MakeVertical = 1L << 7,
        DeleteRenders = 1L << 8,
        /// <summary>Save and remove the team's channels.</summary>
        ManageChannels = 1L << 9,
        /// <summary>The Save audio now button.</summary>
        SaveAudio = 1L << 10,
        /// <summary>Turn the team's automatic audio saving on or off.</summary>
        ManageAudioSettings = 1L << 11,
        /// <summary>Make and revoke invite links.</summary>
        InviteMembers = 1L << 12,
        /// <summary>Give members roles (only roles whose permissions they have themselves).</summary>
        AssignRoles = 1L << 13,
        /// <summary>Create, change and delete roles (only with permissions they have themselves, never their own role).</summary>
        ManageRoles = 1L << 14,
        RemoveMembers = 1L << 15,
        /// <summary>Make password reset links for members who are in no other team.</summary>
        ResetPasswords = 1L << 16,
        /// <summary>See each member's activity, what they worked on and their disk use, and the team's audit log.</summary>
        ViewActivity = 1L << 17,

        All = (1L << 18) - 1,

        /// <summary>Any of these lets a member open the Team page.</summary>
        TeamPage = InviteMembers | AssignRoles | ManageRoles | RemoveMembers | ResetPasswords | ViewActivity | ManageAudioSettings,
    }

    public sealed record TeamPermissionInfo(string Key, string Group, string Label, string Description);

    public static class TeamPermissions
    {
        /// <summary>Names and help text for the role editor, in display order. Keys are the enum names.</summary>
        public static readonly IReadOnlyList<(TeamPermission Permission, TeamPermissionInfo Info)> Catalog =
        [
            (TeamPermission.ViewLibrary, new("ViewLibrary", "Library", "View the library", "See entries, saved channels and VODs; watch, play and download files.")),
            (TeamPermission.AddEntries, new("AddEntries", "Library", "Add VODs and clips", "Add new entries to the library.")),
            (TeamPermission.EditOwnEntries, new("EditOwnEntries", "Library", "Edit own entries", "Rename and re-cut entries they added.")),
            (TeamPermission.EditAnyEntries, new("EditAnyEntries", "Library", "Edit anyone's entries", "Rename and re-cut any entry.")),
            (TeamPermission.DeleteOwnEntries, new("DeleteOwnEntries", "Library", "Delete own entries", "Delete entries they added.")),
            (TeamPermission.DeleteAnyEntries, new("DeleteAnyEntries", "Library", "Delete anyone's entries", "Delete any entry.")),
            (TeamPermission.GenerateFiles, new("GenerateFiles", "Library", "Make files", "Generate files for reference-only entries and retry failed downloads.")),
            (TeamPermission.MakeVertical, new("MakeVertical", "Vertical", "Make vertical videos", "Render 9:16 versions of entries.")),
            (TeamPermission.DeleteRenders, new("DeleteRenders", "Vertical", "Delete vertical renders", "Delete anyone's vertical renders.")),
            (TeamPermission.ManageChannels, new("ManageChannels", "Channels and audio", "Manage saved channels", "Save and remove the team's channels.")),
            (TeamPermission.SaveAudio, new("SaveAudio", "Channels and audio", "Save audio now", "Use the Save audio now button on VODs.")),
            (TeamPermission.ManageAudioSettings, new("ManageAudioSettings", "Channels and audio", "Automatic audio saving", "Turn the team's automatic audio saving on or off.")),
            (TeamPermission.InviteMembers, new("InviteMembers", "Team", "Invite people", "Make and revoke invite links.")),
            (TeamPermission.AssignRoles, new("AssignRoles", "Team", "Give roles", "Give members roles (only roles with permissions they have themselves).")),
            (TeamPermission.ManageRoles, new("ManageRoles", "Team", "Edit roles", "Create, change and delete roles (only with permissions they have themselves).")),
            (TeamPermission.RemoveMembers, new("RemoveMembers", "Team", "Remove members", "Remove people from the team.")),
            (TeamPermission.ResetPasswords, new("ResetPasswords", "Team", "Reset passwords", "Make password reset links for members who aren't in any other team.")),
            (TeamPermission.ViewActivity, new("ViewActivity", "Team", "See member activity", "See what each member works on, how active they are, their disk use and the team log.")),
        ];

        public static IReadOnlyList<string> ToKeys(TeamPermission permissions) =>
            Catalog.Where(x => permissions.HasFlag(x.Permission)).Select(x => x.Info.Key).ToList();

        /// <summary>Null if any key is unknown.</summary>
        public static TeamPermission? FromKeys(IEnumerable<string>? keys)
        {
            var result = TeamPermission.None;
            foreach (var key in keys ?? [])
            {
                var match = Catalog.FirstOrDefault(x => string.Equals(x.Info.Key, key, StringComparison.OrdinalIgnoreCase));
                if (match.Info is null)
                    return null;
                result |= match.Permission;
            }
            return result;
        }

        /// <summary>True if <paramref name="subset"/> grants nothing beyond <paramref name="holder"/>.</summary>
        public static bool IsWithin(TeamPermission subset, TeamPermission holder) => (subset & ~holder) == 0;
    }

    /// <summary>A role a team owner (or a member allowed to) made, with its permissions. Each member has at most one role per team.</summary>
    public class TeamRole
    {
        public int Id { get; set; }
        public int TeamId { get; set; }
        public required string Name { get; set; }
        public TeamPermission Permissions { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
