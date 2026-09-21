using BfaNet.Application.Abstractions;
using BfaNet.Domain;
using BfaNet.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BfaNet.Infrastructure.Persistence;

public sealed class BankDbContext(DbContextOptions<BankDbContext> options, IFieldEncryptor encryptor) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<LedgerTransaction> Transactions => Set<LedgerTransaction>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<Beneficiary> Beneficiaries => Set<Beneficiary>();
    public DbSet<Card> Cards => Set<Card>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
    public DbSet<CustomerAvatar> CustomerAvatars => Set<CustomerAvatar>();
    public DbSet<DeviceCredential> DeviceCredentials => Set<DeviceCredential>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<Enum>().HaveConversion<string>().HaveMaxLength(24);
        b.Properties<decimal>().HavePrecision(19, 2);
    }

    /// <summary>AES-GCM encrypted string column; the column name is bound as AAD.</summary>
    private ValueConverter Encrypted(string column) =>
        new ValueConverter<string, string>(v => encryptor.Encrypt(v, column), v => encryptor.Decrypt(v, column));

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Customer>(e =>
        {
            e.ToTable("customers");
            e.HasKey(x => x.Id);
            e.Property(x => x.CustomerNumber).HasMaxLength(8).IsFixedLength();
            e.Property(x => x.FullName).HasMaxLength(120);
            // PII at rest: AES-256-GCM. Lookups go through the HMAC blind-index columns below.
            e.Property(x => x.Email).HasConversion(Encrypted("customers.email")).HasMaxLength(512);
            e.Property(x => x.Phone).HasConversion(Encrypted("customers.phone")).HasMaxLength(512);
            e.Property(x => x.NationalId).HasConversion(Encrypted("customers.national_id")).HasMaxLength(512);
            e.Property(x => x.TaxId).HasConversion(Encrypted("customers.tax_id")).HasMaxLength(512);
            e.Property(x => x.Address).HasConversion(Encrypted("customers.address")).HasMaxLength(1024);
            e.Property(x => x.EmailHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.PhoneHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.NationalIdHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.PasswordHash).HasMaxLength(256);
            e.Property(x => x.PinHash).HasMaxLength(256);
            e.HasIndex(x => x.CustomerNumber).IsUnique();
            e.HasIndex(x => x.EmailHash).IsUnique();
            e.HasIndex(x => x.PhoneHash).IsUnique();
            e.HasIndex(x => x.NationalIdHash).IsUnique();
            e.HasMany(x => x.Accounts).WithOne(a => a.Customer).HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_customers_number_format", "customer_number ~ '^[0-9]{8}$'"));
        });

        m.Entity<Account>(e =>
        {
            e.ToTable("accounts", t =>
            {
                // Customer accounts can never go negative; only internal GL accounts may.
                t.HasCheckConstraint("ck_accounts_balance_non_negative", "balance >= 0 OR type = 'Interna'");
                t.HasCheckConstraint("ck_accounts_iban_format", "iban ~ '^AO[0-9]{23}$'");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Iban).HasMaxLength(25).IsFixedLength();
            e.Property(x => x.AccountNumber).HasMaxLength(11).IsFixedLength();
            e.Property(x => x.Nickname).HasMaxLength(40);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasIndex(x => x.Iban).IsUnique();
            e.HasIndex(x => x.AccountNumber).IsUnique();
            e.HasIndex(x => x.CustomerId);
        });

        m.Entity<LedgerTransaction>(e =>
        {
            e.ToTable("transactions", t =>
            {
                t.HasCheckConstraint("ck_transactions_amount_positive", "amount > 0");
                t.HasCheckConstraint("ck_transactions_fee_non_negative", "fee >= 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Reference).HasMaxLength(32);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Description).HasMaxLength(140);
            e.Property(x => x.CounterpartyName).HasMaxLength(120);
            e.Property(x => x.CounterpartyIban).HasMaxLength(25);
            e.Property(x => x.ExternalReference).HasMaxLength(64);
            e.HasIndex(x => x.Reference).IsUnique();
            // Idempotency: one logical operation per (customer, key).
            e.HasIndex(x => new { x.InitiatedBy, x.IdempotencyKey }).IsUnique();
            // Daily-limit query.
            e.HasIndex(x => new { x.InitiatedBy, x.CreatedAt }).IsDescending(false, true);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.InitiatedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Entries).WithOne(x => x.Transaction).HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<LedgerEntry>(e =>
        {
            e.ToTable("ledger_entries", t => t.HasCheckConstraint("ck_ledger_entries_amount_positive", "amount > 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            // Statement pages: WHERE account_id = ? AND id < cursor ORDER BY id DESC.
            e.HasIndex(x => new { x.AccountId, x.Id }).IsDescending(false, true);
            // Date-range filtering.
            e.HasIndex(x => new { x.AccountId, x.CreatedAt });
            e.HasIndex(x => x.TransactionId);
        });

        m.Entity<Beneficiary>(e =>
        {
            e.ToTable("beneficiaries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Iban).HasMaxLength(25).IsFixedLength();
            e.Property(x => x.BankName).HasMaxLength(80);
            e.HasIndex(x => new { x.CustomerId, x.Iban }).IsUnique();
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<Card>(e =>
        {
            e.ToTable("cards", t =>
            {
                t.HasCheckConstraint("ck_cards_last4_format", "last4 ~ '^[0-9]{4}$'");
                t.HasCheckConstraint("ck_cards_limit_non_negative", "daily_limit >= 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.ProductName).HasMaxLength(60);
            e.Property(x => x.Last4).HasMaxLength(4).IsFixedLength();
            e.Property(x => x.HolderName).HasMaxLength(120);
            e.HasIndex(x => x.CustomerId);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.DeviceLabel).HasMaxLength(60);
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.CustomerId, x.FamilyId });
            // Housekeeping / session listing only ever touches live rows.
            e.HasIndex(x => x.ExpiresAt).HasFilter("revoked_at IS NULL");
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(512);
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.Property(x => x.UserAgent).HasMaxLength(256);
            e.HasIndex(x => new { x.CustomerId, x.CreatedAt }).IsDescending(false, true);
            e.HasIndex(x => new { x.Action, x.CreatedAt });
        });

        m.Entity<CustomerAvatar>(e =>
        {
            e.ToTable("customer_avatars", t => t.HasCheckConstraint("ck_customer_avatars_size", "octet_length(data) <= 307200"));
            e.HasKey(x => x.CustomerId);
            e.Property(x => x.ContentType).HasMaxLength(32);
            e.HasOne<Customer>().WithOne().HasForeignKey<CustomerAvatar>(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<DeviceCredential>(e =>
        {
            e.ToTable("device_credentials");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.DeviceLabel).HasMaxLength(60);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.CustomerId).HasFilter("revoked_at IS NULL");
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<ExchangeRate>(e =>
        {
            e.ToTable("exchange_rates", t => t.HasCheckConstraint("ck_exchange_rates_positive", "buy > 0 AND sell > 0"));
            e.HasKey(x => x.Currency);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Source).HasMaxLength(80);
            e.Property(x => x.Buy).HasPrecision(19, 4);
            e.Property(x => x.Sell).HasPrecision(19, 4);
        });
    }
}
