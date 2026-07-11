using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiSiteFiller.Migrations
{
    /// <inheritdoc />
    public partial class AddSiteIdToArticlesQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "site_id",
                table: "articles_queue",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "site_id",
                table: "articles_queue");
        }
    }
}
