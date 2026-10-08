using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDatasetStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExportHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DatasetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetFormat = table.Column<int>(type: "INTEGER", nullable: false),
                    Settings = table.Column<string>(type: "TEXT", nullable: false),
                    Files = table.Column<string>(type: "TEXT", nullable: false),
                    RecordCount = table.Column<long>(type: "INTEGER", nullable: false),
                    ExportedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExportHistories_Datasets_DatasetId",
                        column: x => x.DatasetId,
                        principalTable: "Datasets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExportProfiles",
                columns: table => new
                {
                    DatasetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetFormat = table.Column<int>(type: "INTEGER", nullable: false),
                    Settings = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportProfiles", x => x.DatasetId);
                    table.ForeignKey(
                        name: "FK_ExportProfiles_Datasets_DatasetId",
                        column: x => x.DatasetId,
                        principalTable: "Datasets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExportHistories_DatasetId_ExportedAt",
                table: "ExportHistories",
                columns: new[] { "DatasetId", "ExportedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExportHistories");

            migrationBuilder.DropTable(
                name: "ExportProfiles");
        }
    }
}
