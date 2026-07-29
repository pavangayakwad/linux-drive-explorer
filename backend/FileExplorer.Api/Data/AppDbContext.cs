using FileExplorer.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace FileExplorer.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<FileOperationJob> FileOperationJobs => Set<FileOperationJob>();
    public DbSet<TrashItem> TrashItems => Set<TrashItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.Username).IsUnique();
        });

        modelBuilder.Entity<FileOperationJob>(entity =>
        {
            entity.Property(j => j.Type).HasConversion<string>();
            entity.Property(j => j.Status).HasConversion<string>();
            entity.HasIndex(j => j.Status);
        });

        modelBuilder.Entity<TrashItem>(entity =>
        {
            entity.HasIndex(t => t.DeletedAt);
        });
    }
}
