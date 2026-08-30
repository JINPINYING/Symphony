using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphony.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AcquisitionObservability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EligibleSeenAtUtc",
                table: "issues_cache",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_issues_cache_EligibleSeenAtUtc",
                table: "issues_cache",
                column: "EligibleSeenAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_issues_cache_EligibleSeenAtUtc",
                table: "issues_cache");

            migrationBuilder.DropColumn(
                name: "EligibleSeenAtUtc",
                table: "issues_cache");
        }
    }
}
