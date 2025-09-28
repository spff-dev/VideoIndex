using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoIndex.Core.Migrations
{
    /// <inheritdoc />
    public partial class _0001_Initial_Tagging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScanRoots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    LastScannedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanRoots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RootId = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Filename = table.Column<string>(type: "TEXT", nullable: false),
                    Extension = table.Column<string>(type: "TEXT", nullable: true),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LengthSeconds = table.Column<double>(type: "REAL", nullable: true),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    BitRate = table.Column<long>(type: "INTEGER", nullable: true),
                    FrameRate = table.Column<double>(type: "REAL", nullable: true),
                    VideoFormat = table.Column<string>(type: "TEXT", nullable: true),
                    AudioFormat = table.Column<string>(type: "TEXT", nullable: true),
                    AudioBitrate = table.Column<long>(type: "INTEGER", nullable: true),
                    AudioChannels = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceTypes = table.Column<string>(type: "TEXT", nullable: true),
                    StudioName = table.Column<string>(type: "TEXT", nullable: true),
                    OrientationTags = table.Column<string>(type: "TEXT", nullable: true),
                    OtherTags = table.Column<string>(type: "TEXT", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceUsername = table.Column<string>(type: "TEXT", nullable: true),
                    PerformerNames = table.Column<string>(type: "TEXT", nullable: true),
                    PerformerCount = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaFiles_ScanRoots_RootId",
                        column: x => x.RootId,
                        principalTable: "ScanRoots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Thumbnails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Jpeg = table.Column<byte[]>(type: "BLOB", nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Thumbnails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Thumbnails_MediaFiles_MediaFileId",
                        column: x => x.MediaFileId,
                        principalTable: "MediaFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_Path",
                table: "MediaFiles",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_RootId",
                table: "MediaFiles",
                column: "RootId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_Sha256",
                table: "MediaFiles",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_Thumbnails_MediaFileId",
                table: "Thumbnails",
                column: "MediaFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Thumbnails");

            migrationBuilder.DropTable(
                name: "MediaFiles");

            migrationBuilder.DropTable(
                name: "ScanRoots");
        }
    }
}
