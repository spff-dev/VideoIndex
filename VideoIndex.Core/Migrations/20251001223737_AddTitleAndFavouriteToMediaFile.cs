using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoIndex.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddTitleAndFavouriteToMediaFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFavourite",
                table: "MediaFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "MediaFiles",
                type: "TEXT",
                nullable: true);

            // The following index might have been part of your migration.
            // If so, you may need to comment it out if it already exists.
            // If your migration file looks different, just find the
            // CreateIndex call for "IX_MediaFiles_Filename" and comment it out.

            /*
            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_Filename",
                table: "MediaFiles",
                column: "Filename");
            */
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsFavourite",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "MediaFiles");

            // If you commented out the CreateIndex call above,
            // you should also comment out the corresponding DropIndex call below.
            /*
            migrationBuilder.DropIndex(
                name: "IX_MediaFiles_Filename",
                table: "MediaFiles");
            */
        }
    }
}