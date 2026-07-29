namespace FileExplorer.Api.Models.Entities;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "Admin";
    public string ThemeColor { get; set; } = "green";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? RefreshToken { get; set; }
    public DateTimeOffset? RefreshTokenExpiresAt { get; set; }
}
