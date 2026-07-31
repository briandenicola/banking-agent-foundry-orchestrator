using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedDemoTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "demo_transactions",
                columns: new[] { "id", "account_reference", "amount", "currency", "description", "is_suspicious", "merchant", "occurred_at", "status" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), "DEMO-CHECKING-001", 84.27m, "USD", "DEMO-TXN-1001 synthetic grocery purchase", false, "Northwind Market", new DateTimeOffset(new DateTime(2026, 7, 28, 16, 30, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Settled" },
                    { new Guid("10000000-0000-0000-0000-000000000002"), "DEMO-CHECKING-001", 12.40m, "USD", "DEMO-TXN-1002 synthetic transit authorization", false, "Metro Transit", new DateTimeOffset(new DateTime(2026, 7, 30, 8, 15, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Pending" },
                    { new Guid("10000000-0000-0000-0000-000000000003"), "DEMO-CHECKING-001", 249.99m, "USD", "DEMO-TXN-1003 synthetic card-not-present purchase", true, "Alpine Digital", new DateTimeOffset(new DateTime(2026, 7, 29, 22, 5, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Settled" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "demo_transactions",
                keyColumn: "id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "demo_transactions",
                keyColumn: "id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "demo_transactions",
                keyColumn: "id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000003"));
        }
    }
}
