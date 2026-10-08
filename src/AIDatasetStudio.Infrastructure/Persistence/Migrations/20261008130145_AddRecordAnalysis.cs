using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDatasetStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ContextLength",
                table: "Datasets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 4096);

            migrationBuilder.CreateTable(
                name: "RecordAnalyses",
                columns: table => new
                {
                    RecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DatasetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Tokenizer = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RulesVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    QuestionKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    IssueFlags = table.Column<int>(type: "INTEGER", nullable: false),
                    Issues = table.Column<string>(type: "TEXT", nullable: false),
                    AnalyzedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordAnalyses", x => x.RecordId);
                    table.ForeignKey(
                        name: "FK_RecordAnalyses_DatasetRecords_RecordId",
                        column: x => x.RecordId,
                        principalTable: "DatasetRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceSettings", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecordAnalyses_DatasetId_QuestionKey",
                table: "RecordAnalyses",
                columns: new[] { "DatasetId", "QuestionKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecordAnalyses");

            migrationBuilder.DropTable(
                name: "WorkspaceSettings");

            migrationBuilder.DropColumn(
                name: "ContextLength",
                table: "Datasets");
        }
    }
}
