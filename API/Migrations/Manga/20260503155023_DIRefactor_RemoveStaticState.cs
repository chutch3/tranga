using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Migrations.Manga
{
    /// <inheritdoc />
    public partial class DIRefactor_RemoveStaticState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MangaConnector is now a DI singleton — remove the orphaned table.
            // No FK referenced this table (MangaConnectorName was a plain string column),
            // so no foreign key drop is needed before dropping the table.
            // Use IF EXISTS so fresh databases (where this table was never created) don't fail.
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"MangaConnector\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MangaConnector",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SupportedLanguages = table.Column<string[]>(type: "text[]", maxLength: 8, nullable: false),
                    IconUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    BaseUris = table.Column<string[]>(type: "text[]", maxLength: 256, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MangaConnector", x => x.Name);
                });
        }
    }
}
