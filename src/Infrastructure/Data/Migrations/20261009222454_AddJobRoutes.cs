using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LogisticsDispatch.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ApproachMeters",
                table: "Jobs",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "ApproachRouteJson",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TripRouteJson",
                table: "Jobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApproachMeters",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ApproachRouteJson",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "TripRouteJson",
                table: "Jobs");
        }
    }
}
