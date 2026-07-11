using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiSiteFiller.Migrations
{
    /// <inheritdoc />
    public partial class AddMongoImageId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mongo_image_id",
                table: "articles_queue",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "mongo_image_id",
                table: "articles_queue");
        }
    }
}
