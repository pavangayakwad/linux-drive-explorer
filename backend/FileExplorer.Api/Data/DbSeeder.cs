using System.Security.Cryptography;
using FileExplorer.Api.Models.Entities;
using FileExplorer.Api.Options;
using Microsoft.EntityFrameworkCore;

namespace FileExplorer.Api.Data;

public static class DbSeeder
{
    public static async Task SeedAdminAsync(AppDbContext db, AdminSeedOptions options, ILogger logger)
    {
        if (await db.Users.AnyAsync())
        {
            await SyncAdminAsync(db, options, logger);
            return;
        }

        var password = options.Password;
        var generated = string.IsNullOrWhiteSpace(password);
        if (generated)
        {
            password = GenerateRandomPassword();
        }

        var admin = new User
        {
            Username = options.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = "Admin",
        };

        db.Users.Add(admin);
        await db.SaveChangesAsync();

        if (generated)
        {
            logger.LogWarning(
                "No ADMIN_PASSWORD was configured, so a random password was generated for the initial admin user.\n" +
                "  Username: {Username}\n  Password: {Password}\n" +
                "Log in and note this down now - it is only ever shown here, in this startup log, once.",
                options.Username, password);
        }
        else
        {
            logger.LogInformation("Seeded initial admin user '{Username}' from configured credentials.", options.Username);
        }
    }

    /// <summary>
    /// Keeps the initial admin account (the lowest-Id Admin user) in line with the configured
    /// Admin:Username / Admin:Password on every startup. Blank values are ignored.
    /// </summary>
    private static async Task SyncAdminAsync(AppDbContext db, AdminSeedOptions options, ILogger logger)
    {
        var admin = await db.Users.Where(u => u.Role == "Admin").OrderBy(u => u.Id).FirstOrDefaultAsync();
        if (admin is null)
        {
            return;
        }

        var changed = false;

        if (!string.IsNullOrWhiteSpace(options.Username) && admin.Username != options.Username)
        {
            if (await db.Users.AnyAsync(u => u.Username == options.Username && u.Id != admin.Id))
            {
                logger.LogWarning("Configured admin username '{Username}' is already used by another user; not renaming.", options.Username);
            }
            else
            {
                logger.LogInformation("Admin username changed from '{Old}' to '{New}' per configuration.", admin.Username, options.Username);
                admin.Username = options.Username;
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.Password) && !BCrypt.Net.BCrypt.Verify(options.Password, admin.PasswordHash))
        {
            admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(options.Password);
            admin.RefreshToken = null;
            admin.RefreshTokenExpiresAt = null;
            logger.LogInformation("Admin password updated from configuration.");
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync();
        }
    }

    private static string GenerateRandomPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%";
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }

        return new string(chars);
    }
}
