using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PawPrints.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCollaborationInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CollaboratesWithUserId",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Invites",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OwnerUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ConsumedByUserId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invites_Users_ConsumedByUserId",
                        column: x => x.ConsumedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invites_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_CollaboratesWithUserId",
                table: "Users",
                column: "CollaboratesWithUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Invites_ConsumedByUserId",
                table: "Invites",
                column: "ConsumedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Invites_OwnerUserId",
                table: "Invites",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Invites_TokenHash",
                table: "Invites",
                column: "TokenHash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Users_CollaboratesWithUserId",
                table: "Users",
                column: "CollaboratesWithUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_Users_CollaboratesWithUserId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "Invites");

            migrationBuilder.DropIndex(
                name: "IX_Users_CollaboratesWithUserId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CollaboratesWithUserId",
                table: "Users");
        }
    }
}
