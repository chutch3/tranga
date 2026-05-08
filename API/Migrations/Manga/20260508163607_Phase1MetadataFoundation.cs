using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Migrations.Manga
{
    /// <inheritdoc />
    public partial class Phase1MetadataFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.AddColumn<bool>(
                name: "IsBundled",
                table: "Chapters",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MetadataConfidence",
                table: "Chapters",
                type: "integer",
                nullable: true);

            // Initialize MetadataSource for all existing Mangas (default: Connector / Unlinked)
            migrationBuilder.Sql(@"
                UPDATE ""Mangas""
                SET ""MetadataSource_SourceType"" = 0,
                    ""MetadataSource_Status"" = 0
                WHERE ""MetadataSource_SourceType"" IS NULL;
            ");

            // Data migration: tag existing chapters with MetadataConfidence based on VolumeNumber and connector
            // Exact (0): VolumeNumber is set AND manga has a MangaDex connector
            migrationBuilder.Sql(@"
                UPDATE ""Chapters"" c
                SET ""MetadataConfidence"" = 0
                WHERE c.""VolumeNumber"" IS NOT NULL
                  AND EXISTS (
                      SELECT 1 FROM ""MangaConnectorToManga"" mcm
                      WHERE mcm.""ObjId"" = c.""ParentMangaId""
                        AND mcm.""MangaConnectorName"" = 'MangaDex'
                  );
            ");

            // Heuristic (1): VolumeNumber is set AND manga does NOT have a MangaDex connector
            migrationBuilder.Sql(@"
                UPDATE ""Chapters"" c
                SET ""MetadataConfidence"" = 1
                WHERE c.""VolumeNumber"" IS NOT NULL
                  AND c.""MetadataConfidence"" IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM ""MangaConnectorToManga"" mcm
                      WHERE mcm.""ObjId"" = c.""ParentMangaId""
                        AND mcm.""MangaConnectorName"" = 'MangaDex'
                  );
            ");

            // NULL: VolumeNumber is null → MetadataConfidence stays NULL (already the default)
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.DropColumn(
                name: "IsBundled",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "MetadataConfidence",
                table: "Chapters");
        }
    }
}
