using FileExplorer.Api.Data;
using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileExplorer.Api.Services;

public class AuthService(AppDbContext db, ITokenService tokenService, IOptions<JwtOptions> jwtOptions) : IAuthService
{
    private readonly JwtOptions _jwtOptions = jwtOptions.Value;

    public async Task<AuthResponse?> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == username, ct);
        if (user is null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            return null;
        }

        return await IssueTokensAsync(user, ct);
    }

    public async Task<AuthResponse?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.RefreshToken == refreshToken, ct);
        if (user is null || user.RefreshTokenExpiresAt is null || user.RefreshTokenExpiresAt < DateTimeOffset.UtcNow)
        {
            return null;
        }

        return await IssueTokensAsync(user, ct);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.RefreshToken == refreshToken, ct);
        if (user is null)
        {
            return;
        }

        user.RefreshToken = null;
        user.RefreshTokenExpiresAt = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> ChangePasswordAsync(int userId, string newPassword, CancellationToken ct = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return false;
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        // Force re-authentication everywhere else after a password change.
        user.RefreshToken = null;
        user.RefreshTokenExpiresAt = null;
        await db.SaveChangesAsync(ct);
        return true;
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

        user.RefreshToken = refreshToken;
        user.RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(_jwtOptions.RefreshTokenDays);
        await db.SaveChangesAsync(ct);

        return new AuthResponse(accessToken, expiresAt, refreshToken, user.Username, user.Role, user.ThemeColor);
    }
}
