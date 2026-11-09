using TwitchDownloaderWeb.Data;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Endpoints
{
    /// <param name="Url">Only returned when the link is made: the token isn't stored, so it can't be shown again.</param>
    public sealed record LinkDto(int Id, string Kind, int? TeamId, string? TeamName, string? Note, DateTime CreatedAtUtc, DateTime ExpiresAtUtc,
        DateTime? UsedAtUtc, string? UsedBy, DateTime? RevokedAtUtc, string? Url);

    public sealed record CreateInviteRequest(string? Note);

    internal static class LinkUrls
    {
        public const int MaxNoteLength = 200;

        /// <summary>The token goes in the fragment (#), which browsers never send to the server or in Referer headers.</summary>
        public static string Build(HttpRequest request, string token) => $"{request.Scheme}://{request.Host}/join.html#{token}";

        public static (AccountLink Link, string Token) New(AccountLinkKind kind, int? teamId, string? note, DateTime now)
        {
            var (token, hash) = AccountRules.NewLinkToken();
            var link = new AccountLink
            {
                Kind = kind,
                TokenHash = hash,
                TeamId = teamId,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() is { Length: > MaxNoteLength } long_ ? long_[..MaxNoteLength] : note.Trim(),
                CreatedAtUtc = now,
                ExpiresAtUtc = now + AccountRules.LinkLifetime,
            };
            return (link, token);
        }

        public static LinkDto ToDto(AccountLink link, string? usedBy, string? url = null) => new(link.Id, link.Kind.ToString(), link.TeamId, link.Team?.Name,
            link.Note, link.CreatedAtUtc, link.ExpiresAtUtc, link.UsedAtUtc, usedBy, link.RevokedAtUtc, url);
    }
}
