namespace ClientHub.Domain;

public enum UserRole { Admin, Client }
public enum ProjectStatus { Pending, InProgress, InReview, Delivered, Archived }
public enum InvoiceStatus { Draft, Sent, Paid, Overdue, Void }

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public required string FullName { get; set; }
    public UserRole Role { get; set; }
    public Guid? ClientId { get; set; }
    public bool IsActive { get; set; } = true;
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockedUntil { get; set; }
    public Client? Client { get; set; }
}

public sealed class Client
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string CompanyName { get; set; }
    public required string Notes { get; set; }
    public ICollection<User> Users { get; set; } = new List<User>();
    public ICollection<Project> Projects { get; set; } = new List<Project>();
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Pending;
    public DateOnly StartDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public Client? Client { get; set; }
    public ICollection<Milestone> Milestones { get; set; } = new List<Milestone>();
    public ICollection<ProjectFile> Files { get; set; } = new List<ProjectFile>();
    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
}

public sealed class Milestone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid ClientId { get; set; }
    public required string Title { get; set; }
    public DateOnly? DueDate { get; set; }
    public bool IsCompleted { get; set; }
    public int Order { get; set; }
    public Project? Project { get; set; }
}

public sealed class ProjectFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid ClientId { get; set; }
    public required string FileName { get; set; }
    public required string StoredPath { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public Guid UploadedById { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public Project? Project { get; set; }
}

public sealed class Comment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid ClientId { get; set; }
    public Guid AuthorId { get; set; }
    public required string Body { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Project? Project { get; set; }
    public User? Author { get; set; }
}

public sealed class Invoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Guid? ProjectId { get; set; }
    public required string Number { get; set; }
    public DateOnly IssueDate { get; set; }
    public DateOnly DueDate { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public required string Currency { get; set; }
    public decimal Total => Lines.Sum(x => x.Quantity * x.UnitPrice);
    public Client? Client { get; set; }
    public ICollection<InvoiceLineItem> Lines { get; set; } = new List<InvoiceLineItem>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}

public sealed class InvoiceLineItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public required string Description { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public Invoice? Invoice { get; set; }
}

public sealed class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public required string StripePaymentIntentId { get; set; }
    public decimal Amount { get; set; }
    public required string Status { get; set; }
    public DateTime? PaidAt { get; set; }
    public Invoice? Invoice { get; set; }
}

public sealed class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string Type { get; set; }
    public required string Message { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ActivityLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid ClientId { get; set; }
    public Guid ActorId { get; set; }
    public required string Action { get; set; }
    public required string Metadata { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Invitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string FullName { get; set; }
    public required string TokenHash { get; set; }
    public Guid ClientId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }
}

public sealed class PasswordResetToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

public static class InvoiceRules
{
    public static bool CanTransition(InvoiceStatus current, InvoiceStatus next) =>
        (current, next) switch
        {
            (InvoiceStatus.Draft, InvoiceStatus.Sent or InvoiceStatus.Void) => true,
            (InvoiceStatus.Sent, InvoiceStatus.Paid or InvoiceStatus.Overdue or InvoiceStatus.Void) => true,
            (InvoiceStatus.Overdue, InvoiceStatus.Paid or InvoiceStatus.Void) => true,
            _ => current == next
        };
}
