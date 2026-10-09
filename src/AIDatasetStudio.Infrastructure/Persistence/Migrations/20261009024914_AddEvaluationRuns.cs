using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDatasetStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEvaluationRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Raw SQL, like QuestionEmbeddings (see AddSemanticQuestions). One row per finished
            // model evaluation of a dataset: who answered and graded, which questions (the part
            // of the last export's split and the seed), and what it came to; Counts is the JSON
            // array of how many answers got each score, 1 to 5.
            migrationBuilder.Sql("""
                CREATE TABLE EvaluationRuns (
                    Id TEXT NOT NULL PRIMARY KEY,
                    DatasetId TEXT NOT NULL REFERENCES Datasets (Id) ON DELETE CASCADE,
                    Model TEXT NOT NULL,
                    Judge TEXT NOT NULL,
                    Source INTEGER NOT NULL,
                    Seed INTEGER NOT NULL,
                    Answered INTEGER NOT NULL,
                    Scored INTEGER NOT NULL,
                    AverageScore REAL NULL,
                    AverageF1 REAL NULL,
                    Counts TEXT NOT NULL,
                    RanAt INTEGER NOT NULL
                );
                CREATE INDEX IX_EvaluationRuns_DatasetId_RanAt ON EvaluationRuns (DatasetId, RanAt);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE EvaluationRuns;");
        }
    }
}
