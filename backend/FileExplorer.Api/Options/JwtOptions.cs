namespace FileExplorer.Api.Options;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = "FileExplorer.Api";
    public string Audience { get; set; } = "FileExplorer.Client";
    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 7;
}
