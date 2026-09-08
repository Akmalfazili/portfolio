using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Portfolio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshRunMarket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Market",
                table: "RefreshRuns",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshRuns_Trigger_Market_CompletedAt",
                table: "RefreshRuns",
                columns: new[] { "Trigger", "Market", "CompletedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RefreshRuns_Trigger_Market_CompletedAt",
                table: "RefreshRuns");

            migrationBuilder.DropColumn(
                name: "Market",
                table: "RefreshRuns");
        }
    }
}
