using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Apiext.MediaKit.EntityFrameworkCore;

/// <summary>Design-time factory for <c>dotnet ef migrations</c>.</summary>
public sealed class MediaKitDbContextFactory : IDesignTimeDbContextFactory<MediaKitDbContext>
{
    public MediaKitDbContext CreateDbContext(string[] args)
    {
        var basePath = Path.Combine(Directory.GetCurrentDirectory());
        // When invoked from repo root or project dir.
        var projectDir = File.Exists(Path.Combine(basePath, "appsettings.json"))
            ? basePath
            : Path.Combine(basePath, "src", "Apiext.MediaKit");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(projectDir)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var connectionString = configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString) ||
            connectionString.StartsWith("Data Source=App_Data/", StringComparison.OrdinalIgnoreCase))
        {
            var dataDir = Path.Combine(projectDir, "App_Data");
            Directory.CreateDirectory(dataDir);
            var file = string.IsNullOrWhiteSpace(connectionString)
                ? "apiext-mediakit.db"
                : connectionString["Data Source=App_Data/".Length..];
            connectionString = $"Data Source={Path.Combine(dataDir, file)}";
        }

        var options = new DbContextOptionsBuilder<MediaKitDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new MediaKitDbContext(options);
    }
}
