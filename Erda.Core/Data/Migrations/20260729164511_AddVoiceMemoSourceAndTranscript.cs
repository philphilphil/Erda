using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erda.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVoiceMemoSourceAndTranscript : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "upload" backfills existing rows: before this column, only /upload memos were archived.
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "VoiceMemos",
                type: "TEXT",
                nullable: false,
                defaultValue: "upload");

            migrationBuilder.AddColumn<string>(
                name: "Transcript",
                table: "VoiceMemos",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Source",
                table: "VoiceMemos");

            migrationBuilder.DropColumn(
                name: "Transcript",
                table: "VoiceMemos");
        }
    }
}
