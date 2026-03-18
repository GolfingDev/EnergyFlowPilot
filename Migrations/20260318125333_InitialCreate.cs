using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TibberVictronController.Web.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DecisionHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    TargetPowerWatts = table.Column<double>(type: "REAL", nullable: false),
                    CurrentPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnergyStateHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    GridPowerWatts = table.Column<double>(type: "REAL", nullable: false),
                    BatterySocPercent = table.Column<double>(type: "REAL", nullable: false),
                    BatteryPowerWatts = table.Column<double>(type: "REAL", nullable: false),
                    HouseConsumptionWatts = table.Column<double>(type: "REAL", nullable: false),
                    PvPowerWatts = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnergyStateHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionHistory_TimestampUtc",
                table: "DecisionHistory",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_EnergyStateHistory_TimestampUtc",
                table: "EnergyStateHistory",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DecisionHistory");

            migrationBuilder.DropTable(
                name: "EnergyStateHistory");
        }
    }
}
