namespace TwitchDownloaderWeb.Data
{
    /// <summary>What logging in needs from <see cref="UserAccount"/> and <see cref="StaffAccount"/>.</summary>
    public interface ILoginAccount
    {
        int Id { get; }
        string Username { get; }
        string PasswordHash { get; set; }
        string SecurityStamp { get; }
        DateTime? LastLoginAtUtc { get; set; }
        int FailedLoginCount { get; set; }
        DateTime? LockedUntilUtc { get; set; }
        DateTime? DisabledAtUtc { get; }
    }

    /// <summary>
    /// A team member's login (editors, clippers, team owners). One account can be in many teams (<see cref="TeamMember"/>).
    /// App staff (the app Owner and app moderators) are a separate structure: <see cref="StaffAccount"/>.
    /// </summary>
    public class UserAccount : ILoginAccount
    {
        public int Id { get; set; }

        /// <summary>As typed at sign-up; shown on the page.</summary>
        public required string Username { get; set; }

        /// <summary>Lower-case <see cref="Username"/>. Unique; used for login.</summary>
        public required string NormalizedUsername { get; set; }

        public required string PasswordHash { get; set; }

        /// <summary>Changed when the password changes or the account is disabled; sessions carrying an older stamp are signed out.</summary>
        public required string SecurityStamp { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public DateTime? LastLoginAtUtc { get; set; }

        /// <summary>Last request made while logged in (updated at most every few minutes).</summary>
        public DateTime? LastActiveAtUtc { get; set; }

        public int FailedLoginCount { get; set; }
        public DateTime? LockedUntilUtc { get; set; }
        public DateTime? DisabledAtUtc { get; set; }

        public List<TeamMember> Memberships { get; set; } = [];
    }

    /// <summary>A team: its own library, saved channels and members. Saved audio (audio rescues) is shared per VOD across teams.</summary>
    public class Team
    {
        public int Id { get; set; }
        public required string Name { get; set; }
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>Has every team permission and can't be removed. Null until someone opens the team's owner link (e.g. the migrated "Main team").</summary>
        public int? OwnerUserId { get; set; }
        public UserAccount? OwnerUser { get; set; }

        public DateTime? DisabledAtUtc { get; set; }

        public List<TeamMember> Members { get; set; } = [];
    }

    public class TeamMember
    {
        public int Id { get; set; }
        public int TeamId { get; set; }
        public Team? Team { get; set; }
        public int UserId { get; set; }
        public UserAccount? User { get; set; }
        public DateTime JoinedAtUtc { get; set; }
    }

    /// <summary>
    /// App staff: the app Owner (one, set up from the machine running the app) and app moderators. Separate from team users
    /// so app moderation can change without touching team accounts; staff log in on /admin.html.
    /// </summary>
    public class StaffAccount : ILoginAccount
    {
        public int Id { get; set; }
        public required string Username { get; set; }
        public required string NormalizedUsername { get; set; }
        public required string PasswordHash { get; set; }
        public required string SecurityStamp { get; set; }
        public bool IsOwner { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? LastLoginAtUtc { get; set; }
        public int FailedLoginCount { get; set; }
        public DateTime? LockedUntilUtc { get; set; }
        public DateTime? DisabledAtUtc { get; set; }
    }

    public enum AccountLinkKind
    {
        /// <summary>From staff: whoever opens it creates (or logs in to) an account and a new team, and becomes its owner.</summary>
        TeamRegistration,

        /// <summary>From staff: makes whoever opens it the owner of an existing team (claiming the migrated team, hand-over, recovery).</summary>
        TeamOwnership,

        /// <summary>From a team owner: whoever opens it joins the team with no permissions. Never carries a role (badrfly's call).</summary>
        TeamInvite,
    }

    /// <summary>
    /// A one-time link. Only the SHA-256 of the token is stored, so the database alone can't open links; the token itself is shown
    /// once to whoever made the link and is never logged or audited.
    /// </summary>
    public class AccountLink
    {
        public int Id { get; set; }
        public AccountLinkKind Kind { get; set; }

        /// <summary>Lower-case hex SHA-256 of the token. Unique.</summary>
        public required string TokenHash { get; set; }

        /// <summary>The team joined or taken over; null for <see cref="AccountLinkKind.TeamRegistration"/>.</summary>
        public int? TeamId { get; set; }
        public Team? Team { get; set; }

        /// <summary>A label for whoever made it (e.g. who it's for). Shown in link lists only.</summary>
        public string? Note { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public int? CreatedByStaffId { get; set; }
        public int? CreatedByUserId { get; set; }

        public DateTime? UsedAtUtc { get; set; }
        public int? UsedByUserId { get; set; }
        public DateTime? RevokedAtUtc { get; set; }
    }
}
