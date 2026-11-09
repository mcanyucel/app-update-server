using AppUpdateServer.Data.Entities;
using AppUpdateServer.Services;
using Microsoft.EntityFrameworkCore;

namespace AppUpdateServer.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<App> Apps => Set<App>();
    public DbSet<AppVersion> Versions => Set<AppVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<App>(e =>
        {
            e.HasIndex(a => a.Slug).IsUnique();
            e.Property(a => a.Tag).HasMaxLength(UpdateService.MaxTagLength);
            e.HasIndex(a => a.Tag);
            e.HasMany(a => a.Versions)
                .WithOne(v => v.App)
                .HasForeignKey(v => v.AppId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppVersion>(e =>
        {
            e.HasIndex(v => new { v.AppId, v.VersionString }).IsUnique();
            e.HasIndex(v => v.UploadedAt);
        });
    }
}
