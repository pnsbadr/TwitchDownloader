using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;

namespace TwitchDownloaderWeb.Services.Accounts
{
    /// <summary>Username/password rules, password hashing (ASP.NET Core's PBKDF2 hasher) and one-time link tokens.</summary>
    public static partial class AccountRules
    {
        public const int MinPasswordLength = 10;
        public const int MaxPasswordLength = 200;
        public const int MaxFailedLogins = 5;
        public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(7);
        public const int MaxTeamNameLength = 100;

        private static readonly PasswordHasher<object> Hasher = new();
        private static readonly object HashUser = new();

        [GeneratedRegex(@"^[A-Za-z0-9_.\-]{3,32}$")]
        private static partial Regex UsernameRegex();

        [GeneratedRegex(@"^[A-Za-z0-9_\-]{43}$")]
        private static partial Regex TokenRegex();

        public static string? UsernameError(string? username) =>
            username is not null && UsernameRegex().IsMatch(username) ? null : "Usernames are 3–32 letters, numbers, dots, dashes or underscores.";

        public static string? PasswordError(string? password) =>
            password is null || password.Length < MinPasswordLength ? $"Passwords need at least {MinPasswordLength} characters."
            : password.Length > MaxPasswordLength ? $"Passwords can be at most {MaxPasswordLength} characters."
            : null;

        public static string? TeamNameError(string? name) =>
            string.IsNullOrWhiteSpace(name) ? "Enter a team name."
            : name.Trim().Length > MaxTeamNameLength ? $"Team names can be at most {MaxTeamNameLength} characters."
            : null;

        public static string Normalize(string username) => username.ToLowerInvariant();

        public static string HashPassword(string password) => Hasher.HashPassword(HashUser, password);

        /// <summary>True if it matches. <paramref name="rehash"/> is a new hash to store when the hasher's settings got stronger.</summary>
        public static bool VerifyPassword(string hash, string password, out string? rehash)
        {
            rehash = null;
            var result = Hasher.VerifyHashedPassword(HashUser, hash, password);
            if (result == PasswordVerificationResult.SuccessRehashNeeded)
                rehash = HashPassword(password);
            return result != PasswordVerificationResult.Failed;
        }

        /// <summary>A hash of a random password, checked when the username doesn't exist so a failed login takes as long either way.</summary>
        public static readonly string DummyHash = HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));

        public static string NewSecurityStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        /// <summary>A new link token (256 random bits, URL-safe base64, 43 characters) and the hash to store.</summary>
        public static (string Token, string Hash) NewLinkToken()
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return (token, HashToken(token));
        }

        /// <summary>Null for anything that can't be a token, so junk never reaches the database.</summary>
        public static string? HashTokenOrNull(string? token) => token is not null && TokenRegex().IsMatch(token) ? HashToken(token) : null;

        private static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    }
}

namespace TwitchDownloaderWeb.Services.Accounts
{
    public enum LoginOutcome { Success, WrongPassword, LockedOut, Disabled }

    public static class LoginCheck
    {
        /// <summary>
        /// Checks a password and updates the account's lockout counters (caller saves). <paramref name="account"/> null = unknown username,
        /// which still runs a hash so it takes as long as a real one.
        /// </summary>
        public static LoginOutcome Check(Data.ILoginAccount? account, string password, DateTime now)
        {
            if (account is null)
            {
                AccountRules.VerifyPassword(AccountRules.DummyHash, password, out _);
                return LoginOutcome.WrongPassword;
            }

            if (account.LockedUntilUtc > now)
                return LoginOutcome.LockedOut;

            if (!AccountRules.VerifyPassword(account.PasswordHash, password, out var rehash))
            {
                account.FailedLoginCount++;
                if (account.FailedLoginCount >= AccountRules.MaxFailedLogins)
                {
                    account.LockedUntilUtc = now + AccountRules.LockoutDuration;
                    account.FailedLoginCount = 0;
                }
                return LoginOutcome.WrongPassword;
            }

            if (account.DisabledAtUtc is not null)
                return LoginOutcome.Disabled;

            if (rehash is not null)
                account.PasswordHash = rehash;
            account.FailedLoginCount = 0;
            account.LockedUntilUtc = null;
            account.LastLoginAtUtc = now;
            return LoginOutcome.Success;
        }

        public static IResult FailureResult(LoginOutcome outcome) => outcome switch
        {
            LoginOutcome.LockedOut => Results.Problem(detail: $"Too many wrong passwords. Try again in {AccountRules.LockoutDuration.TotalMinutes:0} minutes.",
                statusCode: StatusCodes.Status429TooManyRequests),
            LoginOutcome.Disabled => Results.Problem(detail: "This account is disabled.", statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Problem(detail: "Wrong username or password.", statusCode: StatusCodes.Status401Unauthorized),
        };
    }
}
