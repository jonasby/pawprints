using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PawPrints.Api.Data.Migrations;

/// <summary>
/// Corrects UI "Day N" being one low for all users: tracking day is calendar days from ArrivalDate to log date + 1
/// (see getTrackingDay in src/events.js). Moving arrival one day earlier increments every displayed day number.
/// </summary>
public partial class ShiftArrivalDateForTrackingDay : Migration
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
            UPDATE [Users]
            SET [ArrivalDate] = DATEADD(day, -1, [ArrivalDate]);
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
            """
            UPDATE [Users]
            SET [ArrivalDate] = DATEADD(day, 1, [ArrivalDate]);
            """
        );
    }
}
