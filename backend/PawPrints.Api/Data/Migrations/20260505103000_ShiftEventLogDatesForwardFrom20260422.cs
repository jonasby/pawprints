using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PawPrints.Api.Data.Migrations;

/// <summary>
/// Shifts each puppy log row one calendar day forward from the cutoff onward (DateKey and OccurredAt together),
/// so 22 Apr becomes 23 Apr, 23 Apr becomes 24 Apr, etc., without collapsing multiple days into one.
/// Edit <see cref="CutoffDateInclusive"/> if your data uses a different calendar year.
/// </summary>
public partial class ShiftEventLogDatesForwardFrom20260422 : Migration
{
    /// <summary>First log day (inclusive) to move forward by one day on SQL Server.</summary>
    private const string CutoffDateInclusive = "2026-04-22";

    /// <summary>After Up, former cutoff day is stored as this date — used for Down.</summary>
    private const string FirstDateAfterUp = "2026-04-23";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.SqlServer")
        {
            return;
        }

        migrationBuilder.Sql(
            $"""
            UPDATE [Events]
            SET [DateKey] = DATEADD(day, 1, [DateKey]),
                [OccurredAt] = DATEADD(day, 1, [OccurredAt])
            WHERE [DateKey] >= '{CutoffDateInclusive}';
            """
        );
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.SqlServer")
        {
            return;
        }

        migrationBuilder.Sql(
            $"""
            UPDATE [Events]
            SET [DateKey] = DATEADD(day, -1, [DateKey]),
                [OccurredAt] = DATEADD(day, -1, [OccurredAt])
            WHERE [DateKey] >= '{FirstDateAfterUp}';
            """
        );
    }
}
