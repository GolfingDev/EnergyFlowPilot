using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TibberVictronController.Dal.Persistence;

namespace TibberVictronController.Dal.Tests;

/// <summary>
/// The schema comes from two sources: <c>EnsureCreated</c> builds new databases from the EF model, while
/// <see cref="ControllerDbInitializer"/> upgrades existing ones with hand-written SQL. A table or column added only to
/// the EF model works in tests and on fresh installations but fails on the Raspberry Pi's existing database.
/// </summary>
public sealed class SchemaConsistencyTests : IDisposable
{
    private static readonly DateTimeOffset InitializedAtUtc = new(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);

    // Tables of the first release exist in every database; the initializer never has to create them. Their columns are
    // only checked against a database created from the current model (their original schema is not available here).
    private static readonly HashSet<string> FirstReleaseTables = ["ControllerSettings", "DecisionLogEntries", "DecisionLogReasons", "OperationalEvents"];

    private readonly SqliteConnection connection = new("Data Source=:memory:");

    public SchemaConsistencyTests() => connection.Open();

    public void Dispose() => connection.Dispose();

    [Fact]
    public async Task DatabaseFromBeforeLaterTablesGetsEveryTableAndColumnOfTheModel()
    {
        await using var db = new ControllerDbContext(new DbContextOptionsBuilder<ControllerDbContext>().UseSqlite(connection).Options);
        var initializer = new ControllerDbInitializer(db);
        await initializer.InitializeAsync(InitializedAtUtc);
        var expected = ModelColumns(db.Model);

        // Simulate an existing database from before every later table: only the first-release tables remain.
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        foreach (var table in expected.Keys.Where(table => !FirstReleaseTables.Contains(table)))
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"" + table + "\";");

        await initializer.InitializeAsync(InitializedAtUtc);

        var problems = new List<string>();
        foreach (var (table, columns) in expected)
        {
            var actual = await ColumnsAsync(table);
            if (actual.Count == 0) problems.Add($"{table}: table missing");
            else if (columns.Except(actual).ToArray() is { Length: > 0 } missing) problems.Add($"{table}: {string.Join(", ", missing)} missing");
        }
        Assert.True(problems.Count == 0, "Not created by ControllerDbInitializer for existing databases: " + string.Join("; ", problems));
    }

    private static Dictionary<string, HashSet<string>> ModelColumns(IModel model) => model.GetEntityTypes()
        .Where(entity => entity.GetTableName() is not null)
        .GroupBy(entity => entity.GetTableName()!)
        .ToDictionary(group => group.Key, group => group
            .SelectMany(entity => entity.GetProperties()
                .Select(property => property.GetColumnName(StoreObjectIdentifier.Table(group.Key, entity.GetSchema()))))
            .OfType<string>().ToHashSet(StringComparer.Ordinal));

    private async Task<HashSet<string>> ColumnsAsync(string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", table);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return columns;
    }
}
