namespace FileExplorer.Api.Options;

public class AdminSeedOptions
{
    public const string SectionName = "Admin";

    public string Username { get; set; } = "admin";

    /// <summary>
    /// Blank (the default) makes the seeder generate a random password on first startup.
    /// There is deliberately no well-known default password.
    /// </summary>
    public string Password { get; set; } = string.Empty;
}
