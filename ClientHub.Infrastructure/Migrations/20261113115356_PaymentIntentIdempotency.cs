using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClientHub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PaymentIntentIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Payments_StripePaymentIntentId",
                table: "Payments",
                column: "StripePaymentIntentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_StripePaymentIntentId",
                table: "Payments");
        }
    }
}
