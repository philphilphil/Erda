using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erda.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddErrorWatchSignatureLastAlerted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SignatureLastAlertedJson",
                table: "ErrorWatchState",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SignatureLastAlertedJson",
                table: "ErrorWatchState");
        }
    }
}
