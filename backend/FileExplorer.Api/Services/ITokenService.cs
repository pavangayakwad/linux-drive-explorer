using FileExplorer.Api.Models.Entities;

namespace FileExplorer.Api.Services;

public interface ITokenService
{
    (string Token, DateTimeOffset ExpiresAt) GenerateAccessToken(User user);

    string GenerateRefreshToken();
}
