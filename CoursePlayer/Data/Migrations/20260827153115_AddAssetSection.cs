using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoursePlayer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetSection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Section",
                table: "Assets",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Section",
                table: "Assets");
        }
    }
}
