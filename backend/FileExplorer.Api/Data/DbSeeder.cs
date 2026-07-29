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
