namespace TwitchDownloaderWeb.Data
{
    /// <summary>
    /// What an app moderator may do on the Admin page. The app Owner has all of them and is the only one who manages moderators.
    /// Stored as a bit mask on <see cref="StaffAccount.Permissions"/>: never renumber a value; add new ones at the end.
    /// Separate from <see cref="TeamPermission"/> on purpose: staff and team users are separate structures (badrfly's call).
    /// </summary>
    [Flags]
    public enum StaffPermission : long
    {
        None = 0,

        /// <summary>See every team and user: memberships, activity, last active, disk use.</summary>
        ViewTeamsAndUsers = 1L << 0,

        /// <summary>Make and revoke team registration and team owner links.</summary>
        ManageTeamLinks = 1L << 1,

        /// <summary>Make password reset links for any team user, including people in several teams.</summary>
        ResetUserPasswords = 1L << 2,

        /// <summary>Disable and re-enable team user accounts.</summary>
        DisableUsers = 1L << 3,

        /// <summary>Disable and re-enable teams.</summary>
        DisableTeams = 1L << 4,

        /// <summary>Open a team's library and Team page read-only ("view as support"). Every visit is audited.</summary>
        ViewAsSupport = 1L << 5,

        /// <summary>The app-wide audit log.</summary>
        ViewAuditLog = 1L << 6,

        All = (1L << 7) - 1,
    }

    public sealed record StaffPermissionInfo(string Key, string Label, string Description);

    public static class StaffPermissions
    {
        public static readonly IReadOnlyList<(StaffPermission Permission, StaffPermissionInfo Info)> Catalog =
        [
            (StaffPermission.ViewTeamsAndUsers, new("ViewTeamsAndUsers", "See teams and users",
                "Every team and user: who is in which team, activity, last active and disk use.")),
            (StaffPermission.ViewAsSupport, new("ViewAsSupport", "View as support",
                "Open any team's library and Team page read-only. Every visit is logged.")),
            (StaffPermission.ViewAuditLog, new("ViewAuditLog", "See the app log", "The audit log of every team and of staff.")),
            (StaffPermission.ManageTeamLinks, new("ManageTeamLinks", "Make team links",
                "Team registration and team owner links. An owner link hands a team to whoever opens it.")),
            (StaffPermission.ResetUserPasswords, new("ResetUserPasswords", "Reset user passwords",
                "Password reset links for any user. Whoever opens one gets into that account.")),
            (StaffPermission.DisableUsers, new("DisableUsers", "Disable users", "Disable and re-enable user accounts (signs them out).")),
            (StaffPermission.DisableTeams, new("DisableTeams", "Disable teams", "Disable and re-enable teams (members lose access to them).")),
        ];

        public static IReadOnlyList<string> ToKeys(StaffPermission permissions) =>
            Catalog.Where(x => permissions.HasFlag(x.Permission)).Select(x => x.Info.Key).ToList();

        /// <summary>Null if any key is unknown.</summary>
        public static StaffPermission? FromKeys(IEnumerable<string>? keys)
        {
            var result = StaffPermission.None;
            foreach (var key in keys ?? [])
            {
                var match = Catalog.FirstOrDefault(x => string.Equals(x.Info.Key, key, StringComparison.OrdinalIgnoreCase));
                if (match.Info is null)
                    return null;
                result |= match.Permission;
            }
            return result;
        }
    }
}
