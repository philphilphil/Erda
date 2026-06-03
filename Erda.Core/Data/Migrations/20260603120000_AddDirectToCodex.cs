using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erda.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectToCodex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DirectToCodex",
                table: "Reminders",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DirectToCodex",
                table: "Reminders");
        }
    }
}
