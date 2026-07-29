namespace FileExplorer.Api.Models.Dtos;

public record LoginRequest(string Username, string Password);

public record RefreshRequest(string RefreshToken);

public record AuthResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, string Username, string Role, string ThemeColor);

public record ChangePasswordRequest(string NewPassword);

public record UpdateThemeRequest(string ThemeColor);
