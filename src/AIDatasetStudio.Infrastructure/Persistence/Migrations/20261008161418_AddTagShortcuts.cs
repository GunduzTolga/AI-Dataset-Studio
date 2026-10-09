using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDatasetStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTagShortcuts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Shortcut",
                table: "Tags",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Shortcut",
                table: "Tags",
                column: "Shortcut",
                unique: true,
                filter: "Shortcut IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tags_Shortcut",
                table: "Tags");

            migrationBuilder.DropColumn(
                name: "Shortcut",
                table: "Tags");
        }
    }
}
