using TwitchDownloaderWeb.Data;

namespace TwitchDownloaderWeb.Services.Accounts
{
    /// <summary>
    /// Who may do what with a teammate's vertical edits (Player 2 projects and 9:16 renders; badrfly 2026-11-17). A member always has their
    /// own. Others' are seen with <see cref="TeamPermission.ViewTeamVerticals"/> (or <see cref="TeamPermission.DownloadTeamVerticals"/>, which
    /// includes seeing them), downloaded or exported with DownloadTeamVerticals, edited in place or copied with EditAnyEntries and deleted
    /// with DeleteAnyEntries, each on top of seeing them. Without any, a member starts their own from the original horizontal clip.
    /// A support visit sees everything (read-only anyway). Renders from before their maker was kept (<c>null</c>) count as a teammate's.
    /// Horizontal (Player 2) edits don't follow these rules: they're open to the whole team (see <c>ProjectEndpoints.CanSee</c>).
    /// </summary>
    public static class VerticalAccess
    {
        public static bool IsOwn(TeamContext team, int? userId) => !team.IsSupport && userId is { } id && id == team.UserId;

        /// <summary>May see (list, open, play) teammates' vertical edits at all.</summary>
        public static bool CanSeeTeam(TeamContext team) =>
            team.IsSupport || (team.Permissions & (TeamPermission.ViewTeamVerticals | TeamPermission.DownloadTeamVerticals)) != 0;

        public static bool CanSee(TeamContext team, int? userId) => IsOwn(team, userId) || CanSeeTeam(team);

        public static bool CanDownload(TeamContext team, int? userId) =>
            IsOwn(team, userId) || team.IsSupport || team.Can(TeamPermission.DownloadTeamVerticals);

        /// <summary>Edit a teammate's in place, or make an own copy of it to keep working on.</summary>
        public static bool CanReEditTeam(TeamContext team) => !team.IsSupport && CanSeeTeam(team) && team.Can(TeamPermission.EditAnyEntries);

        public static bool CanDeleteTeam(TeamContext team) => !team.IsSupport && CanSeeTeam(team) && team.Can(TeamPermission.DeleteAnyEntries);
    }
}
