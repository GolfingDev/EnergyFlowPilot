using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Globalization;
using TibberVictronController.Dal.Entities;

namespace TibberVictronController.Dal.Persistence;

public sealed class ControllerDbContext : DbContext
{
    private static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetToUnixMillisecondsConverter = new(
        value => value.ToUnixTimeMilliseconds(),
        value => DateTimeOffset.FromUnixTimeMilliseconds(value));
    private static readonly ValueConverter<DateOnly, string> DateOnlyToIsoDateConverter = new(
        value => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        value => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture));

    public ControllerDbContext(DbContextOptions<ControllerDbContext> options)
        : base(options)
    {
    }

    public DbSet<ControllerSettingEntity> ControllerSettings => Set<ControllerSettingEntity>();

    public DbSet<DecisionLogEntryEntity> DecisionLogEntries => Set<DecisionLogEntryEntity>();

    public DbSet<DecisionLogReasonEntity> DecisionLogReasons => Set<DecisionLogReasonEntity>();

    public DbSet<OperationalEventEntity> OperationalEvents => Set<OperationalEventEntity>();

    public DbSet<BatterySavingsDailySummaryEntity> BatterySavingsDailySummaries => Set<BatterySavingsDailySummaryEntity>();

    public DbSet<LiveConsumptionSampleEntity> LiveConsumptionSamples => Set<LiveConsumptionSampleEntity>();

    public DbSet<ConsumptionDayProfileEntity> ConsumptionDayProfiles => Set<ConsumptionDayProfileEntity>();

    public DbSet<HomematicClimateSampleEntity> HomematicClimateSamples => Set<HomematicClimateSampleEntity>();

    public DbSet<WeishauptBiblockTelemetrySampleEntity> WeishauptBiblockTelemetrySamples => Set<WeishauptBiblockTelemetrySampleEntity>();
    public DbSet<WeishauptHotWaterControlStateEntity> WeishauptHotWaterControlStates => Set<WeishauptHotWaterControlStateEntity>();

    public DbSet<ConsumptionDailySummaryEntity> ConsumptionDailySummaries => Set<ConsumptionDailySummaryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureControllerSettings(modelBuilder);
        ConfigureDecisionLogs(modelBuilder);
        ConfigureOperationalEvents(modelBuilder);
        ConfigureBatterySavings(modelBuilder);
        ConfigureLiveConsumptionSamples(modelBuilder);
        ConfigureConsumptionDayProfiles(modelBuilder);
        ConfigureHomematicClimateSamples(modelBuilder);
        ConfigureWeishauptBiblockTelemetrySamples(modelBuilder);
        modelBuilder.Entity<WeishauptHotWaterControlStateEntity>().ToTable("WeishauptHotWaterControlStates");
        ConfigureConsumptionDailySummaries(modelBuilder);
    }

    private static void ConfigureConsumptionDailySummaries(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConsumptionDailySummaryEntity>(entity =>
        {
            entity.ToTable("ConsumptionDailySummaries");
            entity.HasKey(summary => summary.LocalDate);
            entity.Property(summary => summary.LocalDate).HasMaxLength(10);
            entity.Property(summary => summary.UpdatedAtUtc)
                .HasConversion(DateTimeOffsetToUnixMillisecondsConverter);
        });
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

    private static void ConfigureBatterySavings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BatterySavingsDailySummaryEntity>(entity =>
        {
            entity.ToTable("BatterySavingsDailySummaries");
            entity.HasKey(summary => new { summary.AccountingDate, summary.Currency });
            entity.Property(summary => summary.AccountingDate)
                .HasConversion(DateOnlyToIsoDateConverter)
                .HasMaxLength(10);
            entity.Property(summary => summary.Currency).HasMaxLength(16);
            entity.Property(summary => summary.UpdatedAtUtc)
                .HasConversion(DateTimeOffsetToUnixMillisecondsConverter);
            entity.HasIndex(summary => summary.AccountingDate);
        });
    }

    private static void ConfigureLiveConsumptionSamples(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LiveConsumptionSampleEntity>(entity =>
        {
            entity.ToTable("LiveConsumptionSamples");
            entity.HasKey(sample => sample.Id);
            entity.Property(sample => sample.MeasuredAtUtc)
                .HasConversion(DateTimeOffsetToUnixMillisecondsConverter);
            entity.Property(sample => sample.HouseConsumptionWatts);
            entity.Property(sample => sample.GridPowerWatts);
            entity.Property(sample => sample.BatteryPowerWatts);
            entity.Property(sample => sample.BatterySocPercent);
            entity.Property(sample => sample.PvProductionWatts);
            entity.Property(sample => sample.SupplementaryBatteryPowerWatts);
            entity.Property(sample => sample.SupplementaryBatterySocPercent);
            entity.Property(sample => sample.SupplementaryPvProductionWatts);
            entity.HasIndex(sample => sample.MeasuredAtUtc);
        });
    }

    private static void ConfigureConsumptionDayProfiles(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConsumptionDayProfileEntity>(entity =>
        {
            entity.ToTable("ConsumptionDayProfiles");
            entity.HasKey(profile => new { profile.DayOfWeek, profile.SlotIndex });
            entity.Property(profile => profile.UpdatedAtUtc)
                .HasConversion(DateTimeOffsetToUnixMillisecondsConverter);
            entity.HasIndex(profile => profile.UpdatedAtUtc);
        });
    }

    private static void ConfigureHomematicClimateSamples(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HomematicClimateSampleEntity>(entity =>
        {
            entity.ToTable("HomematicClimateSamples");
            entity.HasKey(sample => sample.Id);
            entity.Property(sample => sample.PolledAtUtc)
                .HasConversion(DateTimeOffsetToUnixMillisecondsConverter);
            entity.Property(sample => sample.LastChangedAtUtc)
                .HasConversion(DateTimeOffsetToUnixMillisecondsConverter);
            entity.Property(sample => sample.ChannelAddress).HasMaxLength(64);
            entity.Property(sample => sample.DeviceType).HasMaxLength(64);
            entity.Property(sample => sample.DeviceName).HasMaxLength(256);
            entity.Property(sample => sample.ChannelName).HasMaxLength(256);
            entity.Property(sample => sample.Kind).HasConversion<string>().HasMaxLength(32);
            entity.Property(sample => sample.SetPointMode).HasConversion<string>().HasMaxLength(32);
            // REAL keeps the column numeric so the series can be analysed directly with SQL.
            entity.Property(sample => sample.ActualTemperatureCelsius).HasConversion<double?>();
            entity.Property(sample => sample.SetPointTemperatureCelsius).HasConversion<double?>();
            entity.Property(sample => sample.ValveOpeningPercent).HasConversion<double?>();
            entity.Property(sample => sample.RelativeHumidityPercent).HasConversion<double?>();
            entity.HasIndex(sample => sample.PolledAtUtc);
            entity.HasIndex(sample => new { sample.ChannelAddress, sample.PolledAtUtc });
        });
    }

    private static void ConfigureWeishauptBiblockTelemetrySamples(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WeishauptBiblockTelemetrySampleEntity>(entity =>
        {
            entity.ToTable("WeishauptBiblockTelemetrySamples");
            entity.HasKey(sample => sample.Id);
            entity.Property(sample => sample.MeasuredAtUtc).HasConversion(DateTimeOffsetToUnixMillisecondsConverter);
            entity.Property(sample => sample.OutdoorTemperatureCelsius).HasConversion<double>();
            entity.Property(sample => sample.HotWaterTemperatureCelsius).HasConversion<double>();
            entity.HasIndex(sample => sample.MeasuredAtUtc);
        });
    }
}
