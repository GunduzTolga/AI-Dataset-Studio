using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDatasetStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSemanticQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Raw SQL, like DatasetSimilarity (see AddNearDuplicates). One vector per distinct
            // question of a dataset, from the dataset's embedding model: signed bytes with one
            // scale, and the random-hyperplane signature the candidates are found with.
            migrationBuilder.Sql("""
                CREATE TABLE QuestionEmbeddings (
                    DatasetId TEXT NOT NULL REFERENCES Datasets (Id) ON DELETE CASCADE,
                    QuestionKey TEXT NOT NULL,
                    Model TEXT NOT NULL,
                    Vector BLOB NOT NULL,
                    Scale REAL NOT NULL,
                    Signature BLOB NOT NULL,
                    PRIMARY KEY (DatasetId, QuestionKey)
                );
                ALTER TABLE DatasetSimilarity ADD COLUMN SemanticModel TEXT NULL;
                ALTER TABLE DatasetSimilarity ADD COLUMN SemanticThreshold REAL NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE QuestionEmbeddings;
                ALTER TABLE DatasetSimilarity DROP COLUMN SemanticModel;
                ALTER TABLE DatasetSimilarity DROP COLUMN SemanticThreshold;
                """);
        }
    }
}
