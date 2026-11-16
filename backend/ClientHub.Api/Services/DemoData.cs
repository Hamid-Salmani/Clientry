using ClientHub.Domain;
using ClientHub.Application;
using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace ClientHub.Api.Services;

public static class DemoData
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClientHubDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        await db.Database.MigrateAsync();
        if (await db.Users.AnyAsync()) return;

        var hasher = new PasswordHasher<User>();
        var clientA = new Client { CompanyName = "Northstar Studio", Notes = "Brand and digital design partner" };
        var clientB = new Client { CompanyName = "Juniper & Co.", Notes = "Advisory client" };
        User NewUser(string email, string name, UserRole role, Guid? clientId)
        {
            var user = new User { Email = email, FullName = name, Role = role, ClientId = clientId, PasswordHash = "" };
            user.PasswordHash = hasher.HashPassword(user, "DemoPass!234");
            return user;
        }
        var admin = NewUser("admin@demo.com", "Alex Morgan", UserRole.Admin, null);
        var userA = NewUser("clienta@demo.com", "Mia Chen", UserRole.Client, clientA.Id);
        var userB = NewUser("clientb@demo.com", "Noah Patel", UserRole.Client, clientB.Id);
        var projectA = new Project
        {
            ClientId = clientA.Id, Name = "Brand identity refresh",
            Description = "A complete visual identity system, from strategy through launch.",
            Status = ProjectStatus.InProgress, StartDate = new DateOnly(2026, 10, 5),
            DueDate = new DateOnly(2026, 12, 12)
        };
        var projectB = new Project
        {
            ClientId = clientB.Id, Name = "Quarterly advisory",
            Description = "Ongoing finance and operations advisory.",
            Status = ProjectStatus.InReview, StartDate = new DateOnly(2026, 9, 15),
            DueDate = new DateOnly(2026, 11, 28)
        };
        var invoiceA = new Invoice
        {
            ClientId = clientA.Id, ProjectId = projectA.Id, Number = "INV-2026-001",
            IssueDate = new DateOnly(2026, 10, 5), DueDate = new DateOnly(2026, 11, 30),
            Status = InvoiceStatus.Sent, Currency = "USD",
            Lines = [new InvoiceLineItem { Description = "Identity design — phase 1", Quantity = 1, UnitPrice = 2400 }]
        };
        var invoiceB = new Invoice
        {
            ClientId = clientB.Id, ProjectId = projectB.Id, Number = "INV-2026-002",
            IssueDate = new DateOnly(2026, 10, 1), DueDate = new DateOnly(2026, 10, 31),
            Status = InvoiceStatus.Paid, Currency = "USD",
            Lines = [new InvoiceLineItem { Description = "October advisory", Quantity = 8, UnitPrice = 180 }]
        };
        db.AddRange(clientA, clientB, admin, userA, userB, projectA, projectB, invoiceA, invoiceB,
            new Milestone { ClientId = clientA.Id, ProjectId = projectA.Id, Title = "Discovery & strategy", Order = 1, IsCompleted = true },
            new Milestone { ClientId = clientA.Id, ProjectId = projectA.Id, Title = "Visual directions", Order = 2, IsCompleted = true },
            new Milestone { ClientId = clientA.Id, ProjectId = projectA.Id, Title = "Identity system", Order = 3 },
            new Milestone { ClientId = clientA.Id, ProjectId = projectA.Id, Title = "Brand guidelines", Order = 4 },
            new Milestone { ClientId = clientB.Id, ProjectId = projectB.Id, Title = "Operations review", Order = 1, IsCompleted = true },
            new Milestone { ClientId = clientB.Id, ProjectId = projectB.Id, Title = "Recommendations", Order = 2, IsCompleted = true },
            new Comment { ClientId = clientA.Id, ProjectId = projectA.Id, AuthorId = admin.Id, Body = "The first visual directions are ready for your review." },
            new Comment { ClientId = clientB.Id, ProjectId = projectB.Id, AuthorId = userB.Id, Body = "Thanks, the recommendations look great." },
            new Payment
            {
                InvoiceId = invoiceB.Id, StripePaymentIntentId = "demo_payment_intent",
                Amount = 1440, Status = "Succeeded", PaidAt = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc)
            },
            new Notification { UserId = userA.Id, Type = "project", Message = "Visual directions are ready for review." },
            new ActivityLog { ClientId = clientA.Id, ProjectId = projectA.Id, ActorId = admin.Id, Action = "Project created", Metadata = "Brand identity refresh" });
        var sampleContent = Encoding.UTF8.GetBytes("ClientHub shared project brief\r\nA short sample file for the demo workspace.\r\n");
        await using var sampleStream = new MemoryStream(sampleContent);
        var storedPath = await storage.SaveAsync(sampleStream, CancellationToken.None);
        db.ProjectFiles.Add(new ProjectFile
        {
            ClientId = clientA.Id, ProjectId = projectA.Id, FileName = "Project-brief.txt",
            StoredPath = storedPath, ContentType = "text/plain", Size = sampleContent.Length, UploadedById = admin.Id
        });
        await using var secondSampleStream = new MemoryStream(sampleContent);
        var secondStoredPath = await storage.SaveAsync(secondSampleStream, CancellationToken.None);
        db.ProjectFiles.Add(new ProjectFile
        {
            ClientId = clientB.Id, ProjectId = projectB.Id, FileName = "Advisory-notes.txt",
            StoredPath = secondStoredPath, ContentType = "text/plain", Size = sampleContent.Length, UploadedById = userB.Id
        });
        await db.SaveChangesAsync();
    }
}
