using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDatasetStudio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReasoningAndToolCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No schema change. The payload JSON gained reasoning, tool calls and the tool calling
            // kind (ADR-052, ADR-053); 1.0 would drop the fields it does not know on the next save
            // and cannot read the new kind, so this marks the files it must refuse (ADR-014, ADR-015).
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
