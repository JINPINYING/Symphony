using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphony.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class PhaseLedgerRunnerHold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HoldReason",
                table: "phase_ledger",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HoldRunner",
                table: "phase_ledger",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HoldSinceUtc",
                table: "phase_ledger",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HoldUntilUtc",
                table: "phase_ledger",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HoldReason",
                table: "phase_ledger");

            migrationBuilder.DropColumn(
                name: "HoldRunner",
                table: "phase_ledger");

            migrationBuilder.DropColumn(
                name: "HoldSinceUtc",
                table: "phase_ledger");

            migrationBuilder.DropColumn(
                name: "HoldUntilUtc",
                table: "phase_ledger");
        }
    }
}
