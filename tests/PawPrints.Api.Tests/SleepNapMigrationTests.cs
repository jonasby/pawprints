using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using PawPrints.Api.Data.Migrations;

namespace PawPrints.Api.Tests;

public sealed class SleepNapMigrationTests
{
    [Fact]
    public void GivenMultipleSleepsOnSameDay_WhenMigrationRuns_ThenOnlyLastSleepStaysSleep()
    {
        var sql = GetUpSql(new ReclassifyEarlierDailySleepsAsNaps());

        Assert.Contains("SET [Type] = N'nap'", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE [Type] = N'sleep'", sql, StringComparison.Ordinal);
        Assert.Contains("PARTITION BY [UserId], [DateKey]", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY [OccurredAt] DESC, [Id] DESC", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE [DailySleeps].[SleepOrder] > 1", sql, StringComparison.Ordinal);
    }

    private static string GetUpSql(Migration migration)
    {
        var migrationBuilder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        var up = migration.GetType().GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(up);

        up.Invoke(migration, [migrationBuilder]);
        var sqlOperation = Assert.Single(migrationBuilder.Operations.OfType<SqlOperation>());
        return sqlOperation.Sql;
    }
}
