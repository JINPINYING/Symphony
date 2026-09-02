using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphony.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RunDispatchContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DirectiveAction",
                table: "runs",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectiveInstructions",
                table: "runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhasePrompt",
                table: "runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhaseRunner",
                table: "runs",
                type: "TEXT",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DirectiveAction",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "DirectiveInstructions",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "PhasePrompt",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "PhaseRunner",
                table: "runs");
        }
    }
}
