using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BfaNet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    detail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_number = table.Column<string>(type: "character(8)", fixedLength: true, maxLength: 8, nullable: false),
                    full_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    email = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    email_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    phone = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    phone_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    national_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    national_id_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    tax_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    address = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    birth_date = table.Column<DateOnly>(type: "date", nullable: false),
                    password_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    pin_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    password_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    lockout_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_pin_count = table.Column<int>(type: "integer", nullable: false),
                    pin_lockout_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                    table.CheckConstraint("ck_customers_number_format", "customer_number ~ '^[0-9]{8}$'");
                });

            migrationBuilder.CreateTable(
                name: "exchange_rates",
                columns: table => new
                {
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    buy = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    sell = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    source = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exchange_rates", x => x.currency);
                    table.CheckConstraint("ck_exchange_rates_positive", "buy > 0 AND sell > 0");
                });

            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    iban = table.Column<string>(type: "character(25)", fixedLength: true, maxLength: 25, nullable: false),
                    account_number = table.Column<string>(type: "character(11)", fixedLength: true, maxLength: 11, nullable: false),
                    type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    balance = table.Column<decimal>(type: "numeric(19,2)", precision: 19, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    nickname = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.CheckConstraint("ck_accounts_balance_non_negative", "balance >= 0 OR type = 'Interna'");
                    table.CheckConstraint("ck_accounts_iban_format", "iban ~ '^AO[0-9]{23}$'");
                    table.ForeignKey(
                        name: "fk_accounts_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beneficiaries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    iban = table.Column<string>(type: "character(25)", fixedLength: true, maxLength: 25, nullable: false),
                    bank_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beneficiaries", x => x.id);
                    table.ForeignKey(
                        name: "fk_beneficiaries_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    session_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replaced_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    initiated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,2)", precision: 19, scale: 2, nullable: false),
                    fee = table.Column<decimal>(type: "numeric(19,2)", precision: 19, scale: 2, nullable: false),
                    description = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    originator_name = table.Column<string>(type: "text", nullable: true),
                    counterparty_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    counterparty_iban = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: true),
                    external_reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transactions", x => x.id);
                    table.CheckConstraint("ck_transactions_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_transactions_fee_non_negative", "fee >= 0");
                    table.ForeignKey(
                        name: "fk_transactions_customers_initiated_by",
                        column: x => x.initiated_by,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    product_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    last4 = table.Column<string>(type: "character(4)", fixedLength: true, maxLength: 4, nullable: false),
                    holder_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    expiry_month = table.Column<int>(type: "integer", nullable: false),
                    expiry_year = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    online_purchases = table.Column<bool>(type: "boolean", nullable: false),
                    contactless = table.Column<bool>(type: "boolean", nullable: false),
                    daily_limit = table.Column<decimal>(type: "numeric(19,2)", precision: 19, scale: 2, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cards", x => x.id);
                    table.CheckConstraint("ck_cards_last4_format", "last4 ~ '^[0-9]{4}$'");
                    table.CheckConstraint("ck_cards_limit_non_negative", "daily_limit >= 0");
                    table.ForeignKey(
                        name: "fk_cards_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cards_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,2)", precision: 19, scale: 2, nullable: false),
                    balance_after = table.Column<decimal>(type: "numeric(19,2)", precision: 19, scale: 2, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_entries", x => x.id);
                    table.CheckConstraint("ck_ledger_entries_amount_positive", "amount > 0");
                    table.ForeignKey(
                        name: "fk_ledger_entries_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ledger_entries_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounts_account_number",
                table: "accounts",
                column: "account_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_accounts_customer_id",
                table: "accounts",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_accounts_iban",
                table: "accounts",
                column: "iban",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_action_created_at",
                table: "audit_logs",
                columns: new[] { "action", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_customer_id_created_at",
                table: "audit_logs",
                columns: new[] { "customer_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_beneficiaries_customer_id_iban",
                table: "beneficiaries",
                columns: new[] { "customer_id", "iban" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cards_account_id",
                table: "cards",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_cards_customer_id",
                table: "cards",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_customers_customer_number",
                table: "customers",
                column: "customer_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customers_email_hash",
                table: "customers",
                column: "email_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customers_national_id_hash",
                table: "customers",
                column: "national_id_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customers_phone_hash",
                table: "customers",
                column: "phone_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_account_id_created_at",
                table: "ledger_entries",
                columns: new[] { "account_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_account_id_id",
                table: "ledger_entries",
                columns: new[] { "account_id", "id" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_transaction_id",
                table: "ledger_entries",
                column: "transaction_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_customer_id_family_id",
                table: "refresh_tokens",
                columns: new[] { "customer_id", "family_id" });

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_expires_at",
                table: "refresh_tokens",
                column: "expires_at",
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transactions_initiated_by_created_at",
                table: "transactions",
                columns: new[] { "initiated_by", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_transactions_initiated_by_idempotency_key",
                table: "transactions",
                columns: new[] { "initiated_by", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transactions_reference",
                table: "transactions",
                column: "reference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "beneficiaries");

            migrationBuilder.DropTable(
                name: "cards");

            migrationBuilder.DropTable(
                name: "exchange_rates");

            migrationBuilder.DropTable(
                name: "ledger_entries");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "transactions");

            migrationBuilder.DropTable(
                name: "customers");
        }
    }
}
