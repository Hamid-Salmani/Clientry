using ClientHub.Application;
using ClientHub.Domain;
using ClientHub.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ClientHub.Tests;

public sealed class DomainAndIsolationTests
{
    [Fact]
    public void InvoiceTotalIsCalculatedFromLineItems()
    {
        var invoice = new Invoice
        {
            Number = "INV-1", Currency = "USD", IssueDate = new DateOnly(2026, 11, 1),
            DueDate = new DateOnly(2026, 11, 30),
            Lines =
            [
                new InvoiceLineItem { Description = "Design", Quantity = 2, UnitPrice = 125.50m },
                new InvoiceLineItem { Description = "Review", Quantity = 1, UnitPrice = 75m }
            ]
        };

        Assert.Equal(326m, invoice.Total);
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft, InvoiceStatus.Sent, true)]
    [InlineData(InvoiceStatus.Draft, InvoiceStatus.Paid, false)]
    [InlineData(InvoiceStatus.Sent, InvoiceStatus.Paid, true)]
    [InlineData(InvoiceStatus.Paid, InvoiceStatus.Void, false)]
    public void InvoiceTransitionsFollowBusinessRules(InvoiceStatus current, InvoiceStatus next, bool expected) =>
        Assert.Equal(expected, InvoiceRules.CanTransition(current, next));

    [Fact]
    public async Task ClientQueryFiltersHideAnotherClientsResources()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clientA = new Client { CompanyName = "A", Notes = "" };
        var clientB = new Client { CompanyName = "B", Notes = "" };
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var projectA = new Project { ClientId = clientA.Id, Name = "A project", Description = "", StartDate = new DateOnly(2026, 1, 1) };
        var projectB = new Project { ClientId = clientB.Id, Name = "B project", Description = "", StartDate = new DateOnly(2026, 1, 1) };
        var invoiceA = new Invoice
        {
            ClientId = clientA.Id, Number = "A-1", Currency = "USD",
            IssueDate = new DateOnly(2026, 1, 1), DueDate = new DateOnly(2026, 2, 1),
            Lines = [new InvoiceLineItem { Description = "Work A", Quantity = 1, UnitPrice = 100 }]
        };
        var invoiceB = new Invoice
        {
            ClientId = clientB.Id, Number = "B-1", Currency = "USD",
            IssueDate = new DateOnly(2026, 1, 1), DueDate = new DateOnly(2026, 2, 1),
            Lines = [new InvoiceLineItem { Description = "Work B", Quantity = 1, UnitPrice = 200 }]
        };

        var adminScope = new TestScope(null, null, false);
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        await using (var seed = new ClientHubDbContext(options, adminScope))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.AddRange(clientA, clientB,
                new User { Id = userA, ClientId = clientA.Id, Email = "a@example.test", FullName = "Client A", Role = UserRole.Client, PasswordHash = "hash" },
                new User { Id = userB, ClientId = clientB.Id, Email = "b@example.test", FullName = "Client B", Role = UserRole.Client, PasswordHash = "hash" },
                projectA, projectB, invoiceA, invoiceB,
                new Milestone { ClientId = clientA.Id, ProjectId = projectA.Id, Title = "A milestone", Order = 1 },
                new Milestone { ClientId = clientB.Id, ProjectId = projectB.Id, Title = "B milestone", Order = 1 },
                new ProjectFile { ClientId = clientA.Id, ProjectId = projectA.Id, FileName = "a.pdf", StoredPath = "a", ContentType = "application/pdf", Size = 1, UploadedById = userA },
                new ProjectFile { ClientId = clientB.Id, ProjectId = projectB.Id, FileName = "b.pdf", StoredPath = "b", ContentType = "application/pdf", Size = 1, UploadedById = Guid.NewGuid() },
                new Comment { ClientId = clientA.Id, ProjectId = projectA.Id, AuthorId = userA, Body = "A comment" },
                new Comment { ClientId = clientB.Id, ProjectId = projectB.Id, AuthorId = userB, Body = "B comment" },
                new Payment { InvoiceId = invoiceA.Id, StripePaymentIntentId = "payment-a", Amount = 100, Status = "Succeeded" },
                new Payment { InvoiceId = invoiceB.Id, StripePaymentIntentId = "payment-b", Amount = 200, Status = "Succeeded" },
                new Notification { UserId = userA, Type = "test", Message = "A notification" },
                new Notification { UserId = userB, Type = "test", Message = "B notification" },
                new ActivityLog { ClientId = clientA.Id, ProjectId = projectA.Id, ActorId = userA, Action = "A", Metadata = "" },
                new ActivityLog { ClientId = clientB.Id, ProjectId = projectB.Id, ActorId = Guid.NewGuid(), Action = "B", Metadata = "" });
            await seed.SaveChangesAsync();
        }

        await using var tenant = new ClientHubDbContext(options, new TestScope(clientA.Id, userA, true));
        Assert.Equal(["A project"], await tenant.Projects.Select(x => x.Name).ToListAsync());
        Assert.Equal(["A milestone"], await tenant.Milestones.Select(x => x.Title).ToListAsync());
        Assert.Equal(["a.pdf"], await tenant.ProjectFiles.OrderByDescending(x => x.UploadedAt).Select(x => x.FileName).ToListAsync());
        Assert.Equal(["A comment"], await tenant.Comments.OrderBy(x => x.CreatedAt).Select(x => x.Body).ToListAsync());
        Assert.Equal(["A-1"], await tenant.Invoices.Select(x => x.Number).ToListAsync());
        Assert.Equal(["Work A"], await tenant.InvoiceLineItems.Select(x => x.Description).ToListAsync());
        Assert.Equal(["payment-a"], await tenant.Payments.Select(x => x.StripePaymentIntentId).ToListAsync());
        Assert.Equal(["A notification"], await tenant.Notifications.Select(x => x.Message).ToListAsync());
        Assert.Equal(["A"], await tenant.ActivityLogs.Select(x => x.Action).ToListAsync());
        Assert.False(await tenant.Projects.AnyAsync(x => x.Id == projectB.Id));
        Assert.False(await tenant.Invoices.AnyAsync(x => x.Id == invoiceB.Id));
    }

    private sealed record TestScope(Guid? ClientId, Guid? UserId, bool IsClient) : IRequestScope;
}
