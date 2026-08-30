using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphony.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class PhaseLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "phase_ledger",
                columns: table => new
                {
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IssueIdentifier = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    PrNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    HeadSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ImplementerRunner = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    RepairCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RejectedHeadSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastVerdict = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    LastVerdictHeadSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_phase_ledger", x => x.IssueId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_phase_ledger_Stage",
                table: "phase_ledger",
                column: "Stage");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "phase_ledger");
        }
    }
}
