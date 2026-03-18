using Microsoft.EntityFrameworkCore;
using TibberVictronController.Web.Models;

namespace TibberVictronController.Web.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<DecisionHistoryEntry> DecisionHistory => Set<DecisionHistoryEntry>();
    public DbSet<EnergyStateHistoryEntry> EnergyStateHistory => Set<EnergyStateHistoryEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DecisionHistoryEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TimestampUtc);
            entity.Property(x => x.Action).IsRequired();
            entity.Property(x => x.Reason).IsRequired();
            entity.Property(x => x.TimestampUtc)
                .HasConversion(
                    v => v.ToUniversalTime(),
                    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        });

        modelBuilder.Entity<EnergyStateHistoryEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TimestampUtc);
            entity.Property(x => x.TimestampUtc)
                .HasConversion(
                    v => v.ToUniversalTime(),
                    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        });
    }
}
