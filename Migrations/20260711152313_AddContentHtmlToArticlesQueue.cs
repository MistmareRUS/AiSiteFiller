using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiSiteFiller.Migrations
{
    /// <inheritdoc />
    public partial class AddContentHtmlToArticlesQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_html",
                table: "articles_queue",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "content_html",
                table: "articles_queue");
        }
    }
}
