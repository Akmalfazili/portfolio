using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Portfolio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackfillCoverageState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "CoveredFrom",
                table: "AssetDividendStates",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssetPriceHistoryStates",
                columns: table => new
                {
                    AssetId = table.Column<int>(type: "int", nullable: false),
                    LastAttemptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastRunSuccess = table.Column<bool>(type: "bit", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CoveredFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    CoveredTo = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetPriceHistoryStates", x => x.AssetId);
                    table.ForeignKey(
                        name: "FK_AssetPriceHistoryStates_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FxPairBackfillStates",
                columns: table => new
                {
                    Base = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Quote = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    LastAttemptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastRunSuccess = table.Column<bool>(type: "bit", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CoveredFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    CoveredTo = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FxPairBackfillStates", x => new { x.Base, x.Quote });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssetPriceHistoryStates");

            migrationBuilder.DropTable(
                name: "FxPairBackfillStates");

            migrationBuilder.DropColumn(
                name: "CoveredFrom",
                table: "AssetDividendStates");
        }
    }
}
