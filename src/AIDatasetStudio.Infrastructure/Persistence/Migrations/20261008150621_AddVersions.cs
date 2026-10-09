using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDatasetStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "VersionId",
                table: "ExportHistories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VersionNumber",
                table: "ExportHistories",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DatasetVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DatasetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Tokenizer = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ContextLength = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordCount = table.Column<long>(type: "INTEGER", nullable: false),
                    ApprovedCount = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatasetVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatasetVersions_Datasets_DatasetId",
                        column: x => x.DatasetId,
                        principalTable: "Datasets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatasetVersions_DatasetId_Number",
                table: "DatasetVersions",
                columns: new[] { "DatasetId", "Number" },
                unique: true);

            // Versions (see Versions/VersionService.cs). Not part of the EF model: they are written and
            // read in bulk through SQL only.
            //
            // A revision is one content a record had, kept for the versions that include it, with the
            // record's lineage so a deleted record can be brought back. It has no foreign key to the
            // record: a version keeps its contents after the record is deleted.
            migrationBuilder.Sql("""
                CREATE TABLE RecordRevisions (
                    Id INTEGER PRIMARY KEY,
                    DatasetId TEXT NOT NULL REFERENCES Datasets (Id) ON DELETE CASCADE,
                    RecordId TEXT NOT NULL,
                    ContentHash TEXT NOT NULL,
                    DataPayload TEXT NOT NULL,
                    Preview TEXT NOT NULL,
                    SourceKind INTEGER NOT NULL,
                    SourceReference TEXT NULL,
                    CreatedBy TEXT NULL,
                    GeneratedByModel TEXT NULL,
                    MetadataJson TEXT NOT NULL,
                    RecordCreatedAt INTEGER NOT NULL,
                    CreatedAt INTEGER NOT NULL,
                    UNIQUE (RecordId, ContentHash)
                );
                """);
            migrationBuilder.Sql("CREATE INDEX IX_RecordRevisions_DatasetId ON RecordRevisions (DatasetId);");

            // A version's records: the revision, status, position and tags of each, and its token count
            // with the version's tokenizer. Ids only, so a version of 100k records stays small.
            migrationBuilder.Sql("""
                CREATE TABLE VersionRecordMappings (
                    VersionId TEXT NOT NULL REFERENCES DatasetVersions (Id) ON DELETE CASCADE,
                    Ordinal INTEGER NOT NULL,
                    RevisionId INTEGER NOT NULL REFERENCES RecordRevisions (Id),
                    Status INTEGER NOT NULL,
                    Tokens INTEGER NULL,
                    TagIds TEXT NULL,
                    PRIMARY KEY (VersionId, Ordinal, RevisionId)
                ) WITHOUT ROWID;
                """);
            migrationBuilder.Sql("CREATE INDEX IX_VersionRecordMappings_RevisionId ON VersionRecordMappings (RevisionId);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS VersionRecordMappings;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS RecordRevisions;");

            migrationBuilder.DropTable(
                name: "DatasetVersions");

            migrationBuilder.DropColumn(
                name: "VersionId",
                table: "ExportHistories");

            migrationBuilder.DropColumn(
                name: "VersionNumber",
                table: "ExportHistories");
        }
    }
}
