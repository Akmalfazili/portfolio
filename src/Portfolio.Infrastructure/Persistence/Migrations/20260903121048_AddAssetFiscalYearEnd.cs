using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Portfolio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetFiscalYearEnd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FiscalYearEndDay",
                table: "Assets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FiscalYearEndMonth",
                table: "Assets",
                type: "int",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Assets",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "FiscalYearEndDay", "FiscalYearEndMonth" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                table: "Assets",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "FiscalYearEndDay", "FiscalYearEndMonth" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                table: "Assets",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "FiscalYearEndDay", "FiscalYearEndMonth" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                table: "Assets",
                keyColumn: "Id",
                keyValue: 4,
                columns: new[] { "FiscalYearEndDay", "FiscalYearEndMonth" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                table: "Assets",
                keyColumn: "Id",
                keyValue: 5,
                columns: new[] { "FiscalYearEndDay", "FiscalYearEndMonth" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                table: "Assets",
                keyColumn: "Id",
                keyValue: 6,
                columns: new[] { "FiscalYearEndDay", "FiscalYearEndMonth" },
                values: new object[] { null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FiscalYearEndDay",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "FiscalYearEndMonth",
                table: "Assets");
        }
    }
}
