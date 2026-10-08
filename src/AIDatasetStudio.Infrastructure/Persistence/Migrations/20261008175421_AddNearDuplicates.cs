using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDatasetStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNearDuplicates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "NearGroup",
                table: "RecordAnalyses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Signature",
                table: "RecordAnalyses",
                type: "BLOB",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecordAnalyses_DatasetId_NearGroup",
                table: "RecordAnalyses",
                columns: new[] { "DatasetId", "NearGroup" },
                filter: "NearGroup IS NOT NULL");

            // Raw SQL, like the other version tables (see AddVersions). The stamp identifies the
            // questions the near-duplicate groups were last made from, so the grouping pass runs
            // only when one changed. A version keeps each record's group, so it exports the same split.
            migrationBuilder.Sql("""
                CREATE TABLE DatasetSimilarity (
                    DatasetId TEXT NOT NULL PRIMARY KEY REFERENCES Datasets (Id) ON DELETE CASCADE,
                    Stamp TEXT NOT NULL
                );
                ALTER TABLE VersionRecordMappings ADD COLUMN NearGroup TEXT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE DatasetSimilarity;
                ALTER TABLE VersionRecordMappings DROP COLUMN NearGroup;
                """);

            migrationBuilder.DropIndex(
                name: "IX_RecordAnalyses_DatasetId_NearGroup",
                table: "RecordAnalyses");

            migrationBuilder.DropColumn(
                name: "NearGroup",
                table: "RecordAnalyses");

            migrationBuilder.DropColumn(
                name: "Signature",
                table: "RecordAnalyses");
        }
    }
}
