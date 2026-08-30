using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphony.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class DirectiveLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "directive_log",
                columns: table => new
                {
                    CommentId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IssueId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IssueIdentifier = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_directive_log", x => x.CommentId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_directive_log_IssueId",
                table: "directive_log",
                column: "IssueId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "directive_log");
        }
    }
}
