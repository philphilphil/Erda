using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erda.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PromptVersions_IsActive",
                table: "PromptVersions");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "PromptVersions",
                type: "TEXT",
                nullable: false,
                defaultValue: "system");

            migrationBuilder.CreateIndex(
                name: "IX_PromptVersions_Kind_IsActive",
                table: "PromptVersions",
                columns: new[] { "Kind", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PromptVersions_Kind_IsActive",
                table: "PromptVersions");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "PromptVersions");

            migrationBuilder.CreateIndex(
                name: "IX_PromptVersions_IsActive",
                table: "PromptVersions",
                column: "IsActive");
        }
    }
}
