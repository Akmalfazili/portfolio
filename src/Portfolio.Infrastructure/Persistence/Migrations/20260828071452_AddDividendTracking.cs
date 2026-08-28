using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Portfolio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDividendTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssetDividendStates",
                columns: table => new
                {
                    AssetId = table.Column<int>(type: "int", nullable: false),
                    LastAttemptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastRunSuccess = table.Column<bool>(type: "bit", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetDividendStates", x => x.AssetId);
                    table.ForeignKey(
                        name: "FK_AssetDividendStates_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DividendEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetId = table.Column<int>(type: "int", nullable: false),
                    ExDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountPerShare = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DividendEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DividendEvents_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DividendEvents_AssetId_ExDate",
                table: "DividendEvents",
                columns: new[] { "AssetId", "ExDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssetDividendStates");

            migrationBuilder.DropTable(
                name: "DividendEvents");
        }
    }
}
