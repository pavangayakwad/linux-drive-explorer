namespace FileExplorer.Api.Options;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = "FileExplorer.Api";
    public string Audience { get; set; } = "FileExplorer.Client";
    public int AccessTokenMinutes { get; set; } = 30;
    /// <summary>
    /// Refresh tokens are reissued with a fresh expiry on every use (see AuthService.IssueTokensAsync),
    /// so a long window here means the session effectively lasts until the user explicitly logs out,
    /// as long as the browser reopens the app at least once within this many days.
    /// </summary>
    public int RefreshTokenDays { get; set; } = 3650;
}
