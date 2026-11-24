using System.Security.Cryptography;
using System.Text;
using FileExplorer.Api.Data;
using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileExplorer.Api.Services;

public class AuthService(AppDbContext db, ITokenService tokenService, IOptions<JwtOptions> jwtOptions, ILogger<AuthService> logger) : IAuthService
{
    /// <summary>BCrypt work factor for newly created hashes (the library default is 11).</summary>
    public const int BcryptWorkFactor = 12;

    private const int FreeAttempts = 5;
    private static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(30);

    // Verified against when the username doesn't exist, so "unknown user" costs the same CPU time as
    // "wrong password" and the response time can't be used to enumerate valid usernames.
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("dummy-password-for-timing", BcryptWorkFactor);

    private readonly JwtOptions _jwtOptions = jwtOptions.Value;

    public static string HashPassword(string password) => BCrypt.Net.BCrypt.HashPassword(password, BcryptWorkFactor);

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<LoginOutcome> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        username = (username ?? string.Empty).Trim();
        password ??= string.Empty;
        // BCrypt only reads 72 bytes; refuse absurd inputs outright rather than hash megabytes.
        if (username.Length is 0 or > 128 || password.Length is 0 or > 256)
        {
            BCrypt.Net.BCrypt.Verify("x", DummyHash);
            return LoginOutcome.Invalid;
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == username, ct);
        if (user is null)
        {
            BCrypt.Net.BCrypt.Verify(password, DummyHash);
            logger.LogWarning("Failed login for unknown user '{Username}'.", Sanitize(username));
            return LoginOutcome.Invalid;
        }

        var now = DateTimeOffset.UtcNow;
        if (user.LockoutUntil is { } until && until > now)
        {
            // Still burn the hash time so a locked account isn't distinguishable by latency.
            BCrypt.Net.BCrypt.Verify(password, DummyHash);
            logger.LogWarning("Login rejected for locked-out user '{Username}' until {Until:u}.", user.Username, until);
            return LoginOutcome.LockedOut(until - now);
        }

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= FreeAttempts)
            {
                // 5th failure -> 30s, then doubling (1m, 2m, 4m ...) capped at MaxLockout.
                var exponent = Math.Min(user.FailedLoginCount - FreeAttempts, 10);
                var delay = TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, exponent), MaxLockout.TotalSeconds));
                user.LockoutUntil = now + delay;
                logger.LogWarning("User '{Username}' locked out for {Delay} after {Count} failed logins.", user.Username, delay, user.FailedLoginCount);
            }
            else
            {
                logger.LogWarning("Failed login for user '{Username}' ({Count} consecutive).", user.Username, user.FailedLoginCount);
            }
            await db.SaveChangesAsync(ct);
            return LoginOutcome.Invalid;
        }

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        // Transparently upgrade hashes created with a weaker work factor.
        if (BCrypt.Net.BCrypt.PasswordNeedsRehash(user.PasswordHash, BcryptWorkFactor))
        {
            user.PasswordHash = HashPassword(password);
        }

        logger.LogInformation("User '{Username}' logged in.", user.Username);
        return LoginOutcome.Success(await IssueTokensAsync(user, ct));
    }

    public async Task<AuthResponse?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 512)
        {
            return null;
        }

        var hash = HashToken(refreshToken);
        var user = await db.Users.SingleOrDefaultAsync(u => u.RefreshToken == hash, ct);
        if (user is null || user.RefreshTokenExpiresAt is null || user.RefreshTokenExpiresAt < DateTimeOffset.UtcNow)
        {
            return null;
        }

        return await IssueTokensAsync(user, ct);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var hash = HashToken(refreshToken);
        var user = await db.Users.SingleOrDefaultAsync(u => u.RefreshToken == hash, ct);
        if (user is null)
        {
            return;
        }

        user.RefreshToken = null;
        user.RefreshTokenExpiresAt = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task<ChangePasswordResult> ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return ChangePasswordResult.NotFound;
        }

        // A stolen access token alone must not be enough to take over the account.
        if (string.IsNullOrEmpty(currentPassword) || !BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
        {
            logger.LogWarning("Change-password rejected for '{Username}': wrong current password.", user.Username);
            return ChangePasswordResult.WrongCurrentPassword;
        }

        user.PasswordHash = HashPassword(newPassword);
        // Force re-authentication everywhere else after a password change.
        user.RefreshToken = null;
        user.RefreshTokenExpiresAt = null;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Password changed for '{Username}'.", user.Username);
        return ChangePasswordResult.Success;
    }

    public async Task<bool> UpdateThemeColorAsync(int userId, string themeColor, CancellationToken ct = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return false;
        }

        user.ThemeColor = themeColor;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<AuthResponse> IssueTokensAsync(Models.Entities.User user, CancellationToken ct)
    {
        var (accessToken, expiresAt) = tokenService.GenerateAccessToken(user);
        var refreshToken = tokenService.GenerateRefreshToken();

        // Only the hash is persisted; the raw token goes to the client once.
        user.RefreshToken = HashToken(refreshToken);
        user.RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(_jwtOptions.RefreshTokenDays);
        await db.SaveChangesAsync(ct);

        return new AuthResponse(accessToken, expiresAt, refreshToken, user.Username, user.Role, user.ThemeColor);
    }

    // Usernames in logs come from attacker-controlled input - strip control characters (log forging).
    private static string Sanitize(string value)
    {
        var trimmed = value.Length > 64 ? value[..64] : value;
        return new string(trimmed.Select(c => char.IsControl(c) ? '?' : c).ToArray());
    }
}

public record LoginOutcome(AuthResponse? Response, TimeSpan? RetryAfter)
{
    public static readonly LoginOutcome Invalid = new(null, null);
    public static LoginOutcome Success(AuthResponse response) => new(response, null);
    public static LoginOutcome LockedOut(TimeSpan retryAfter) => new(null, retryAfter);
}

public enum ChangePasswordResult
{
    Success,
    NotFound,
    WrongCurrentPassword,
}
