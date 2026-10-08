using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDatasetStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Preview",
                table: "DatasetRecords",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            // Full-text search (see Search/SearchIndex.cs). Not part of the EF model: EF Core cannot
            // describe FTS5 tables. RowId is an INTEGER PRIMARY KEY so it survives VACUUM, unlike the
            // implicit rowid of DatasetRecords.
            migrationBuilder.Sql("""
                CREATE TABLE RecordSearchKeys (
                    RowId INTEGER PRIMARY KEY,
                    RecordId TEXT NOT NULL UNIQUE
                );
                """);
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE RecordSearch USING fts5(
                    Text,
                    content = '',
                    contentless_delete = 1,
                    tokenize = 'unicode61 remove_diacritics 2'
                );
                """);

            // Foreign-key cascades fire triggers too, so deleting a dataset or project also
            // removes its records from the index.
            migrationBuilder.Sql("""
                CREATE TRIGGER DatasetRecords_RemoveFromSearch AFTER DELETE ON DatasetRecords
                BEGIN
                    DELETE FROM RecordSearch WHERE rowid = (SELECT RowId FROM RecordSearchKeys WHERE RecordId = OLD.Id);
                    DELETE FROM RecordSearchKeys WHERE RecordId = OLD.Id;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS DatasetRecords_RemoveFromSearch;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS RecordSearch;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS RecordSearchKeys;");

            migrationBuilder.DropColumn(
                name: "Preview",
                table: "DatasetRecords");
        }
    }
}
