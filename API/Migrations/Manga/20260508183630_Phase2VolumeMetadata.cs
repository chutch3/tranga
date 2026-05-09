using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Migrations.Manga
{
    /// <inheritdoc />
    public partial class Phase2VolumeMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Use IF EXISTS — DIRefactor_RemoveStaticState may have already dropped this on existing DBs.
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"MangaConnector\"");

            migrationBuilder.DropColumn(
                name: "MetadataSource_ExternalId",
                table: "Mangas");

            migrationBuilder.DropColumn(
                name: "MetadataSource_LastSyncedAt",
                table: "Mangas");

            migrationBuilder.DropColumn(
                name: "MetadataSource_MatchScore",
                table: "Mangas");

            migrationBuilder.DropColumn(
                name: "MetadataSource_SourceType",
                table: "Mangas");

            migrationBuilder.DropColumn(
                name: "MetadataSource_Status",
                table: "Mangas");

            // AddManga_IsTracked migration may have already added this column on existing DBs.
            migrationBuilder.Sql("ALTER TABLE \"Mangas\" ADD COLUMN IF NOT EXISTS \"IsTracked\" boolean NOT NULL DEFAULT FALSE");

            migrationBuilder.CreateTable(
                name: "MetadataSources",
                columns: table => new
                {
                    MangaId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MatchScore = table.Column<float>(type: "real", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetadataSources", x => x.MangaId);
                    table.ForeignKey(
                        name: "FK_MetadataSources_Mangas_MangaId",
                        column: x => x.MangaId,
                        principalTable: "Mangas",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VolumeMetadata",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MangaId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VolumeNumber = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ArchiveFileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolumeMetadata", x => x.Key);
                    table.ForeignKey(
                        name: "FK_VolumeMetadata_Mangas_MangaId",
                        column: x => x.MangaId,
                        principalTable: "Mangas",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VolumeMetadata_MangaId",
                table: "VolumeMetadata",
                column: "MangaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetadataSources");

            migrationBuilder.DropTable(
                name: "VolumeMetadata");

            migrationBuilder.DropColumn(
                name: "IsTracked",
                table: "Mangas");

            migrationBuilder.AddColumn<string>(
                name: "MetadataSource_ExternalId",
                table: "Mangas",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MetadataSource_LastSyncedAt",
                table: "Mangas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "MetadataSource_MatchScore",
                table: "Mangas",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetadataSource_SourceType",
                table: "Mangas",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetadataSource_Status",
                table: "Mangas",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MangaConnector",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BaseUris = table.Column<string[]>(type: "text[]", maxLength: 256, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IconUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    SupportedLanguages = table.Column<string[]>(type: "text[]", maxLength: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MangaConnector", x => x.Name);
                });
        }
    }
}
