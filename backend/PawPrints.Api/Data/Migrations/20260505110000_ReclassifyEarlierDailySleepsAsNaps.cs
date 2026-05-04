using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PawPrints.Api.Data.Migrations;

/// <summary>
/// Reclassifies duplicate daily sleep entries so only the latest sleep per user per log day remains a sleep.
/// </summary>
public partial class ReclassifyEarlierDailySleepsAsNaps : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.SqlServer")
        {
            return;
        }

        migrationBuilder.Sql(
            """
            WITH [DailySleeps] AS
            (
                SELECT
                    [Id],
                    ROW_NUMBER() OVER (
                        PARTITION BY [UserId], [DateKey]
                        ORDER BY [OccurredAt] DESC, [Id] DESC
                    ) AS [SleepOrder]
                FROM [Events]
                WHERE [Type] = N'sleep'
            )
            UPDATE [Events]
            SET [Type] = N'nap'
            FROM [Events]
            INNER JOIN [DailySleeps]
                ON [Events].[Id] = [DailySleeps].[Id]
            WHERE [DailySleeps].[SleepOrder] > 1;
            """
        );
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // This data migration is intentionally irreversible: existing naps cannot be distinguished
        // from sleep rows that were reclassified by Up.
    }
}
