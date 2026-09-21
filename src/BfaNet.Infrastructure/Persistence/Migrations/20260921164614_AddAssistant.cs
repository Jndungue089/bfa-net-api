using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BfaNet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "loans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal = table.Column<decimal>(type: "numeric(19,2)", precision: 19, scale: 2, nullable: false),
                    term_months = table.Column<int>(type: "integer", nullable: false),
                    annual_rate_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    origination_fee = table.Column<decimal>(type: "numeric(19,2)", precision: 19, scale: 2, nullable: false),
                    installment = table.Column<decimal>(type: "numeric(19,2)", precision: 19, scale: 2, nullable: false),
                    total_repayable = table.Column<decimal>(type: "numeric(19,2)", precision: 19, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    disbursement_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loans", x => x.id);
                    table.CheckConstraint("ck_loans_installment_positive", "installment > 0");
                    table.CheckConstraint("ck_loans_principal_positive", "principal > 0");
                    table.CheckConstraint("ck_loans_total_covers_principal", "total_repayable >= principal");
                    table.ForeignKey(
                        name: "fk_loans_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loans_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loan_installments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,2)", precision: 19, scale: 2, nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    payment_transaction_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loan_installments", x => x.id);
                    table.CheckConstraint("ck_loan_installments_amount_positive", "amount > 0");
                    table.ForeignKey(
                        name: "fk_loan_installments_loans_loan_id",
                        column: x => x.loan_id,
                        principalTable: "loans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_loan_installments_loan_id_number",
                table: "loan_installments",
                columns: new[] { "loan_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_loans_account_id",
                table: "loans",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ux_loans_one_active_per_customer",
                table: "loans",
                column: "customer_id",
                unique: true,
                filter: "status = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "loan_installments");

            migrationBuilder.DropTable(
                name: "loans");
        }
    }
}
