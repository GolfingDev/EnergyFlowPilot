using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TibberVictronController.Dal.Entities;

namespace TibberVictronController.Dal.Persistence;

public sealed class ControllerDbContext : DbContext
{
    private static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetToUnixMillisecondsConverter = new(
        value => value.ToUnixTimeMilliseconds(),
        value => DateTimeOffset.FromUnixTimeMilliseconds(value));

    public ControllerDbContext(DbContextOptions<ControllerDbContext> options)
        : base(options)
    {
    }

    public DbSet<ControllerSettingEntity> ControllerSettings => Set<ControllerSettingEntity>();

    public DbSet<DecisionLogEntryEntity> DecisionLogEntries => Set<DecisionLogEntryEntity>();

    public DbSet<DecisionLogReasonEntity> DecisionLogReasons => Set<DecisionLogReasonEntity>();

    public DbSet<OperationalEventEntity> OperationalEvents => Set<OperationalEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureControllerSettings(modelBuilder);
        ConfigureDecisionLogs(modelBuilder);
        ConfigureOperationalEvents(modelBuilder);
    }

    private static void ConfigureControllerSettings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ControllerSettingEntity>(entity =>
        {
            entity.ToTable("ControllerSettings");
            entity.HasKey(setting => setting.Key);
            entity.Property(setting => setting.Key).HasMaxLength(160);
            entity.Property(setting => setting.Value).HasMaxLength(4000);
            entity.Property(setting => setting.Sensitivity).HasConversion<string>().HasMaxLength(32);
            entity.Property(setting => setting.UpdatedAtUtc)
                .HasConversion(DateTimeOffsetToUnixMillisecondsConverter)
                .IsRequired();
        });
    }

    private static void ConfigureDecisionLogs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DecisionLogEntryEntity>(entity =>
        {
            entity.ToTable("DecisionLogEntries");
            entity.HasKey(logEntry => logEntry.Id);
            entity.Property(logEntry => logEntry.DecisionState).HasConversion<string>().HasMaxLength(32);
            entity.Property(logEntry => logEntry.ChargeSource).HasConversion<string>().HasMaxLength(32);
            entity.Property(logEntry => logEntry.TibberPriceCurrency).HasMaxLength(16);
            entity.Property(logEntry => logEntry.InputSummaryJson).HasMaxLength(16000);
            entity.Property(logEntry => logEntry.DecidedAtUtc)
                .HasConversion(DateTimeOffsetToUnixMillisecondsConverter);
            entity.Property(logEntry => logEntry.ValidFromUtc)
                .HasConversion(DateTimeOffsetToUnixMillisecondsConverter);
            entity.Property(logEntry => logEntry.ValidToUtc)
                .HasConversion(DateTimeOffsetToUnixMillisecondsConverter);
            entity.HasIndex(logEntry => logEntry.DecidedAtUtc);
            entity.HasMany(logEntry => logEntry.Reasons)
                .WithOne(reason => reason.DecisionLogEntry)
                .HasForeignKey(reason => reason.DecisionLogEntryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DecisionLogReasonEntity>(entity =>
        {
            entity.ToTable("DecisionLogReasons");
            entity.HasKey(reason => reason.Id);
            entity.Property(reason => reason.RuleName).HasMaxLength(160);
            entity.Property(reason => reason.Message).HasMaxLength(4000);
            entity.HasIndex(reason => reason.DecisionLogEntryId);
        });
    }

    private static void ConfigureOperationalEvents(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OperationalEventEntity>(entity =>
        {
            entity.ToTable("OperationalEvents");
            entity.HasKey(operationalEvent => operationalEvent.Id);
            entity.Property(operationalEvent => operationalEvent.Category).HasMaxLength(160);
            entity.Property(operationalEvent => operationalEvent.Severity).HasMaxLength(32);
            entity.Property(operationalEvent => operationalEvent.Message).HasMaxLength(4000);
            entity.Property(operationalEvent => operationalEvent.Details).HasMaxLength(16000);
            entity.Property(operationalEvent => operationalEvent.OccurredAtUtc)
                .HasConversion(DateTimeOffsetToUnixMillisecondsConverter);
            entity.HasIndex(operationalEvent => operationalEvent.OccurredAtUtc);
        });
    }
}
