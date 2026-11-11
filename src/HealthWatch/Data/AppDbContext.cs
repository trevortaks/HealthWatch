using Microsoft.EntityFrameworkCore;

namespace HealthWatch.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Target> Targets => Set<Target>();
    public DbSet<CheckResult> Results => Set<CheckResult>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Settings> SettingsRows => Set<Settings>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // SQLite drops DateTimeKind; everything is stored and read as UTC.
        b.Properties<DateTime>().HaveConversion<UtcConverter>();
        b.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<CheckResult>().HasIndex(r => new { r.TargetId, r.At });
        m.Entity<Incident>().HasIndex(i => new { i.TargetId, i.EndedAt });
    }

    /// <summary>EnsureCreated never alters existing tables, so add columns introduced after the first release.</summary>
    public async Task UpgradeSchemaAsync()
    {
        var conn = Database.GetDbConnection();
        await conn.OpenAsync();
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pragma_table_info('SettingsRows')";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) existing.Add(r.GetString(0));
        }
        foreach (var col in new[] { "SmsPopTokenEnc", "SmsPopSenderId" })
            if (!existing.Contains(col))
#pragma warning disable EF1002 // column names are constants above
                await Database.ExecuteSqlRawAsync($"ALTER TABLE SettingsRows ADD COLUMN \"{col}\" TEXT NULL");
#pragma warning restore EF1002
    }

    public async Task<Settings> GetSettingsAsync(CancellationToken ct = default)
    {
        var s = await SettingsRows.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (s is null) { s = new Settings(); SettingsRows.Add(s); await SaveChangesAsync(ct); }
        return s;
    }

    class UtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
        v => v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
    class NullableUtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? v.Value.ToUniversalTime() : v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
