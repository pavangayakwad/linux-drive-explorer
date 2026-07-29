using FileExplorer.Api.Models.Dtos;

namespace FileExplorer.Api.Services;

public interface IAuthService
{
    Task<AuthResponse?> LoginAsync(string username, string password, CancellationToken ct = default);

    Task<AuthResponse?> RefreshAsync(string refreshToken, CancellationToken ct = default);

    Task LogoutAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Returns false if the user wasn't found.</summary>
    Task<bool> ChangePasswordAsync(int userId, string newPassword, CancellationToken ct = default);

    /// <summary>Returns false if the user wasn't found.</summary>
    Task<bool> UpdateThemeColorAsync(int userId, string themeColor, CancellationToken ct = default);
}
