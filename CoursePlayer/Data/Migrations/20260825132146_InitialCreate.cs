using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoursePlayer.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Courses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    ThumbnailPath = table.Column<string>(type: "TEXT", nullable: true),
                    FolderPath = table.Column<string>(type: "TEXT", nullable: false),
                    ImportedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastOpenedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Courses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Assets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CourseId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Duration = table.Column<long>(type: "INTEGER", nullable: true),
                    Codec = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Resolution = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    IsOnline = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Assets_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Progresses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false),
                    WatchedSeconds = table.Column<double>(type: "REAL", nullable: false),
                    LastPage = table.Column<int>(type: "INTEGER", nullable: true),
                    LastAccessedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Progresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Progresses_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_CourseId_FilePath",
                table: "Assets",
                columns: new[] { "CourseId", "FilePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assets_CourseId_OrderIndex",
                table: "Assets",
                columns: new[] { "CourseId", "OrderIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_FilePath",
                table: "Assets",
                column: "FilePath");

            migrationBuilder.CreateIndex(
                name: "IX_Courses_FolderPath",
                table: "Courses",
                column: "FolderPath");

            migrationBuilder.CreateIndex(
                name: "IX_Courses_IsFavorite",
                table: "Courses",
                column: "IsFavorite");

            migrationBuilder.CreateIndex(
                name: "IX_Courses_LastOpenedAt",
                table: "Courses",
                column: "LastOpenedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Progresses_AssetId",
                table: "Progresses",
                column: "AssetId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Progresses_LastAccessedAt",
                table: "Progresses",
                column: "LastAccessedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Progresses");

            migrationBuilder.DropTable(
                name: "Assets");

            migrationBuilder.DropTable(
                name: "Courses");
        }
    }
}
