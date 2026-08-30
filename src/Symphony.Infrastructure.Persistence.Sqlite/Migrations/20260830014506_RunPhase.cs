using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Symphony.Infrastructure.Persistence.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RunPhase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every run that existed before this migration was an implementation dispatch.
            migrationBuilder.AddColumn<string>(
                name: "Phase",
                table: "runs",
                type: "TEXT",
                nullable: false,
                defaultValue: "implementation");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Phase",
                table: "runs");
        }
    }
}
