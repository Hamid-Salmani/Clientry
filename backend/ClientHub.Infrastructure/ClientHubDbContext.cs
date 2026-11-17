using ClientHub.Application;
using ClientHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClientHub.Infrastructure;

public sealed class ClientHubDbContext(DbContextOptions<ClientHubDbContext> options, IRequestScope scope)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<ProjectFile> ProjectFiles => Set<ProjectFile>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLineItem> InvoiceLineItems => Set<InvoiceLineItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<UserNotificationPreference> NotificationPreferences => Set<UserNotificationPreference>();
    public DbSet<WorkspaceSettings> WorkspaceSettings => Set<WorkspaceSettings>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().HasIndex(x => x.Email).IsUnique();
        modelBuilder.Entity<Payment>().HasIndex(x => x.StripePaymentIntentId).IsUnique();
        modelBuilder.Entity<Invitation>().HasIndex(x => x.TokenHash).IsUnique();
        modelBuilder.Entity<RefreshToken>().HasIndex(x => x.TokenHash).IsUnique();
        modelBuilder.Entity<PasswordResetToken>().HasIndex(x => x.TokenHash).IsUnique();
        modelBuilder.Entity<Client>().HasMany(x => x.Users).WithOne(x => x.Client)
            .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<RefreshToken>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PasswordResetToken>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserNotificationPreference>().HasKey(x => x.UserId);
        modelBuilder.Entity<UserNotificationPreference>().HasOne(x => x.User).WithOne(x => x.NotificationPreference)
            .HasForeignKey<UserNotificationPreference>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<User>().HasQueryFilter(x =>
            !scope.IsClient || (scope.ClientId.HasValue && x.ClientId == scope.ClientId));
        modelBuilder.Entity<Client>().HasQueryFilter(x =>
            !scope.IsClient || (scope.ClientId.HasValue && x.Id == scope.ClientId));
        modelBuilder.Entity<Invitation>().HasQueryFilter(x =>
            !scope.IsClient || (scope.ClientId.HasValue && x.ClientId == scope.ClientId));
        modelBuilder.Entity<RefreshToken>().HasQueryFilter(x =>
            !scope.IsClient || (scope.UserId.HasValue && x.UserId == scope.UserId));
        modelBuilder.Entity<PasswordResetToken>().HasQueryFilter(x =>
            !scope.IsClient || (scope.UserId.HasValue && x.UserId == scope.UserId));
        modelBuilder.Entity<Project>().HasQueryFilter(x =>
            !scope.IsClient || (scope.ClientId.HasValue && x.ClientId == scope.ClientId));
        modelBuilder.Entity<Milestone>().HasQueryFilter(x =>
            !scope.IsClient || (scope.ClientId.HasValue && x.ClientId == scope.ClientId));
        modelBuilder.Entity<ProjectFile>().HasQueryFilter(x =>
            !scope.IsClient || (scope.ClientId.HasValue && x.ClientId == scope.ClientId));
        modelBuilder.Entity<Comment>().HasQueryFilter(x =>
            !scope.IsClient || (scope.ClientId.HasValue && x.ClientId == scope.ClientId));
        modelBuilder.Entity<Invoice>().HasQueryFilter(x =>
            !scope.IsClient || (scope.ClientId.HasValue && x.ClientId == scope.ClientId));
        modelBuilder.Entity<InvoiceLineItem>().HasQueryFilter(x =>
            !scope.IsClient || (scope.ClientId.HasValue && x.Invoice != null && x.Invoice.ClientId == scope.ClientId));
        modelBuilder.Entity<Payment>().HasQueryFilter(x =>
            !scope.IsClient || (scope.ClientId.HasValue && x.Invoice != null && x.Invoice.ClientId == scope.ClientId));
        modelBuilder.Entity<Notification>().HasQueryFilter(x =>
            !scope.IsClient || (scope.UserId.HasValue && x.UserId == scope.UserId));
        modelBuilder.Entity<UserNotificationPreference>().HasQueryFilter(x =>
            !scope.IsClient || (scope.ClientId.HasValue && scope.UserId.HasValue && x.UserId == scope.UserId));
        modelBuilder.Entity<ActivityLog>().HasQueryFilter(x =>
            !scope.IsClient || (scope.ClientId.HasValue && x.ClientId == scope.ClientId));
        modelBuilder.Entity<Invoice>().Ignore(x => x.Total);
        modelBuilder.Entity<InvoiceLineItem>().Property(x => x.UnitPrice).HasPrecision(18, 2);
        modelBuilder.Entity<InvoiceLineItem>().Property(x => x.Quantity).HasPrecision(18, 2);
        modelBuilder.Entity<InvoiceLineItem>().HasOne(x => x.Invoice).WithMany(x => x.Lines)
            .HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Payment>().HasOne(x => x.Invoice).WithMany(x => x.Payments)
            .HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}
