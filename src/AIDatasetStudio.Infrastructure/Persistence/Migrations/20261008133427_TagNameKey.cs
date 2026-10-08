using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDatasetStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TagNameKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tags_Name",
                table: "Tags");

            migrationBuilder.AddColumn<string>(
                name: "NameKey",
                table: "Tags",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            // Existing tags get SQLite's ASCII lower(); no released version creates tags yet, and new
            // or renamed tags get the full Turkish-aware key from Tag.FoldName.
            migrationBuilder.Sql("UPDATE Tags SET NameKey = lower(Name);");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_NameKey",
                table: "Tags",
                column: "NameKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tags_NameKey",
                table: "Tags");

            migrationBuilder.DropColumn(
                name: "NameKey",
                table: "Tags");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Name",
                table: "Tags",
                column: "Name",
                unique: true);
        }
    }
}
