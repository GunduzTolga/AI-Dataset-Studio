using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDatasetStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJudgments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecordConflicts",
                columns: table => new
                {
                    RecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OtherId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DatasetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OtherHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Contradicts = table.Column<bool>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CheckedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordConflicts", x => new { x.RecordId, x.OtherId });
                    table.ForeignKey(
                        name: "FK_RecordConflicts_DatasetRecords_OtherId",
                        column: x => x.OtherId,
                        principalTable: "DatasetRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecordConflicts_DatasetRecords_RecordId",
                        column: x => x.RecordId,
                        principalTable: "DatasetRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecordJudgments",
                columns: table => new
                {
                    RecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DatasetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Flags = table.Column<int>(type: "INTEGER", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    JudgedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordJudgments", x => x.RecordId);
                    table.ForeignKey(
                        name: "FK_RecordJudgments_DatasetRecords_RecordId",
                        column: x => x.RecordId,
                        principalTable: "DatasetRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecordConflicts_DatasetId",
                table: "RecordConflicts",
                column: "DatasetId");

            migrationBuilder.CreateIndex(
                name: "IX_RecordConflicts_OtherId",
                table: "RecordConflicts",
                column: "OtherId");

            migrationBuilder.CreateIndex(
                name: "IX_RecordJudgments_DatasetId_Score",
                table: "RecordJudgments",
                columns: new[] { "DatasetId", "Score" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecordConflicts");

            migrationBuilder.DropTable(
                name: "RecordJudgments");
        }
    }
}
