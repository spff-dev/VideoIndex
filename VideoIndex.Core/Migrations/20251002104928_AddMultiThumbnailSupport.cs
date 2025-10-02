using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoIndex.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiThumbnailSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "Thumbnails",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Height",
                table: "Thumbnails",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SequenceNumber",
                table: "Thumbnails",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Width",
                table: "Thumbnails",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Format",
                table: "Thumbnails");

            migrationBuilder.DropColumn(
                name: "Height",
                table: "Thumbnails");

            migrationBuilder.DropColumn(
                name: "SequenceNumber",
                table: "Thumbnails");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "Thumbnails");
        }
    }
}
