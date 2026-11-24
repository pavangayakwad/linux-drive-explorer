namespace FileExplorer.Api.Models.Entities;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "Admin";
    public string ThemeColor { get; set; } = "green";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>SHA-256 (hex) of the current refresh token - the raw token is never stored.</summary>
    public string? RefreshToken { get; set; }
    public DateTimeOffset? RefreshTokenExpiresAt { get; set; }

    /// <summary>Consecutive failed logins since the last success; drives the exponential lockout.</summary>
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
}
