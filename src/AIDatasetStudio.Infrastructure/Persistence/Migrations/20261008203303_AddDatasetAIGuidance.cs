using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDatasetStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDatasetAIGuidance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AIGuidance",
                table: "Datasets",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AIGuidance",
                table: "Datasets");
        }
    }
}
