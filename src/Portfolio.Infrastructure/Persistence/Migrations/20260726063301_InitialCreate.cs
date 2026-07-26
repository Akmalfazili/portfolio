using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Portfolio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Assets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Symbol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AssetClass = table.Column<int>(type: "int", nullable: false),
                    Exchange = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    ProviderSymbol = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ProviderCoinId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FxRates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Base = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Quote = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FxRates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RefreshRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Trigger = table.Column<int>(type: "int", nullable: false),
                    AssetClass = table.Column<int>(type: "int", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SymbolsRefreshed = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Close = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceHistories_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceQuotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetId = table.Column<int>(type: "int", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    AsOf = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceQuotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriceQuotes_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Transactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    TradeDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    PricePerUnit = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    Fees = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Transactions_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Assets",
                columns: new[] { "Id", "AssetClass", "Currency", "Exchange", "IsActive", "Name", "ProviderCoinId", "ProviderSymbol", "Symbol" },
                values: new object[,]
                {
                    { 1, 0, "USD", "NASDAQ", true, "Apple Inc.", null, "AAPL", "AAPL" },
                    { 2, 0, "USD", "NASDAQ", true, "Microsoft Corporation", null, "MSFT", "MSFT" },
                    { 3, 0, "SGD", "SGX", true, "Singapore Telecommunications Limited", null, "Z74:XSES", "Z74" },
                    { 4, 1, "USD", null, true, "Ethereum", "ethereum", null, "ETH" },
                    { 5, 1, "USD", null, true, "Amp", "amp-token", null, "AMP" },
                    { 6, 1, "USD", null, true, "Anvil", "anvil", null, "ANVL" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_Symbol",
                table: "Assets",
                column: "Symbol",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FxRates_Date_Base_Quote",
                table: "FxRates",
                columns: new[] { "Date", "Base", "Quote" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceHistories_AssetId_Date",
                table: "PriceHistories",
                columns: new[] { "AssetId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceQuotes_AssetId",
                table: "PriceQuotes",
                column: "AssetId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshRuns_StartedAt",
                table: "RefreshRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_AssetId_TradeDate",
                table: "Transactions",
                columns: new[] { "AssetId", "TradeDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FxRates");

            migrationBuilder.DropTable(
                name: "PriceHistories");

            migrationBuilder.DropTable(
                name: "PriceQuotes");

            migrationBuilder.DropTable(
                name: "RefreshRuns");

            migrationBuilder.DropTable(
                name: "Transactions");

            migrationBuilder.DropTable(
                name: "Assets");
        }
    }
}
