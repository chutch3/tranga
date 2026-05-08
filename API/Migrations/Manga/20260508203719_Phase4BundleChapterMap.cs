using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Migrations.Manga
{
    /// <inheritdoc />
    public partial class Phase4BundleChapterMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BundleChapterMaps",
                columns: table => new
                {
                    VolumeKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChapterKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartPage = table.Column<int>(type: "integer", nullable: false),
                    PageCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BundleChapterMaps", x => new { x.VolumeKey, x.ChapterKey });
                    table.ForeignKey(
                        name: "FK_BundleChapterMaps_Chapters_ChapterKey",
                        column: x => x.ChapterKey,
                        principalTable: "Chapters",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BundleChapterMaps_VolumeMetadata_VolumeKey",
                        column: x => x.VolumeKey,
                        principalTable: "VolumeMetadata",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BundleChapterMaps_ChapterKey",
                table: "BundleChapterMaps",
                column: "ChapterKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BundleChapterMaps");
        }
    }
}
