using ClientHub.Domain;
using ClientHub.Application;
using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace ClientHub.Api.Services;

public static class DemoData
{
    private const string DemoPassword = "DemoPass!234";

    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClientHubDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        await db.Database.MigrateAsync();

        var hasher = new PasswordHasher<User>();
        var demoEmails = new[] { "admin@demo.com", "clienta@demo.com", "clientb@demo.com" };
        var demoUsers = await db.Users
            .Where(user => demoEmails.Contains(user.Email.ToLower()))
            .ToListAsync();

        foreach (var user in demoUsers)
        {
            user.Email = user.Email.Trim();
            user.PasswordHash = hasher.HashPassword(user, DemoPassword);
            user.FailedLoginAttempts = 0;
            user.LockedUntil = null;
            user.IsActive = true;
        }

        if (!await db.Users.AnyAsync())
        {
            var seedClientA = new Client { CompanyName = "Northstar Studio", Notes = "Brand and digital design partner" };
            var seedClientB = new Client { CompanyName = "Juniper & Co.", Notes = "Advisory client" };

            User NewUser(string email, string name, UserRole role, Guid? clientId)
            {
                var user = new User { Email = email, FullName = name, Role = role, ClientId = clientId, PasswordHash = string.Empty };
                user.PasswordHash = hasher.HashPassword(user, DemoPassword);
                return user;
            }

            var admin = NewUser("admin@demo.com", "Alex Morgan", UserRole.Admin, null);
            var userA = NewUser("clienta@demo.com", "Mia Chen", UserRole.Client, seedClientA.Id);
            var userB = NewUser("clientb@demo.com", "Noah Patel", UserRole.Client, seedClientB.Id);
            var projectA = new Project
            {
                ClientId = seedClientA.Id, Name = "Brand identity refresh",
                Description = "A complete visual identity system, from strategy through launch.",
                Status = ProjectStatus.InProgress, StartDate = new DateOnly(2026, 10, 5),
                DueDate = new DateOnly(2026, 12, 12)
            };
            var projectB = new Project
            {
                ClientId = seedClientB.Id, Name = "Quarterly advisory",
                Description = "Ongoing finance and operations advisory.",
                Status = ProjectStatus.InReview, StartDate = new DateOnly(2026, 9, 15),
                DueDate = new DateOnly(2026, 11, 28)
            };
            var invoiceA = new Invoice
            {
                ClientId = seedClientA.Id, ProjectId = projectA.Id, Number = "INV-2026-001",
                IssueDate = new DateOnly(2026, 10, 5), DueDate = new DateOnly(2026, 11, 30),
                Status = InvoiceStatus.Sent, Currency = "USD",
                Lines = [new InvoiceLineItem { Description = "Identity design — phase 1", Quantity = 1, UnitPrice = 2400 }]
            };
            var invoiceB = new Invoice
            {
                ClientId = seedClientB.Id, ProjectId = projectB.Id, Number = "INV-2026-002",
                IssueDate = new DateOnly(2026, 10, 1), DueDate = new DateOnly(2026, 10, 31),
                Status = InvoiceStatus.Paid, Currency = "USD",
                Lines = [new InvoiceLineItem { Description = "October advisory", Quantity = 8, UnitPrice = 180 }]
            };
            db.AddRange(seedClientA, seedClientB, admin, userA, userB, projectA, projectB, invoiceA, invoiceB,
                new Milestone { ClientId = seedClientA.Id, ProjectId = projectA.Id, Title = "Discovery & strategy", Order = 1, IsCompleted = true },
                new Milestone
                {
                    ClientId = seedClientA.Id, ProjectId = projectA.Id, Title = "Visual directions", Order = 2,
                    IsCompleted = true, ApprovalStatus = MilestoneApprovalStatus.Approved,
                    RequestedAt = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc),
                    DecidedById = userA.Id, DecidedAt = new DateTime(2026, 10, 21, 10, 0, 0, DateTimeKind.Utc)
                },
                new Milestone
                {
                    ClientId = seedClientA.Id, ProjectId = projectA.Id, Title = "Identity system", Order = 3,
                    ApprovalStatus = MilestoneApprovalStatus.Pending,
                    RequestedAt = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc)
                },
                new Milestone
                {
                    ClientId = seedClientA.Id, ProjectId = projectA.Id, Title = "Brand guidelines", Order = 4,
                    ApprovalStatus = MilestoneApprovalStatus.ChangesRequested,
                    RequestedAt = new DateTime(2026, 10, 28, 10, 0, 0, DateTimeKind.Utc),
                    DecidedById = userA.Id, DecidedAt = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc),
                    DecisionNote = "Please include the accessible color contrast variations."
                },
                new Milestone { ClientId = seedClientB.Id, ProjectId = projectB.Id, Title = "Operations review", Order = 1, IsCompleted = true },
                new Milestone { ClientId = seedClientB.Id, ProjectId = projectB.Id, Title = "Recommendations", Order = 2, IsCompleted = true },
                new Comment { ClientId = seedClientA.Id, ProjectId = projectA.Id, AuthorId = admin.Id, Body = "The first visual directions are ready for your review." },
                new Comment { ClientId = seedClientB.Id, ProjectId = projectB.Id, AuthorId = userB.Id, Body = "Thanks, the recommendations look great." },
                new Payment
                {
                    InvoiceId = invoiceB.Id, StripePaymentIntentId = "demo_payment_intent",
                    Amount = 1440, Status = "Succeeded", PaidAt = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc)
                },
                new Notification { UserId = userA.Id, Type = "project", Message = "Visual directions are ready for review." },
                new ActivityLog { ClientId = seedClientA.Id, ProjectId = projectA.Id, ActorId = admin.Id, Action = "Project created", Metadata = "Brand identity refresh" });

            var sampleContent = Encoding.UTF8.GetBytes("ClientHub shared project brief\r\nA short sample file for the demo workspace.\r\n");
            await using var sampleStream = new MemoryStream(sampleContent);
            var storedPath = await storage.SaveAsync(sampleStream, CancellationToken.None);
            db.ProjectFiles.Add(new ProjectFile
            {
                ClientId = seedClientA.Id, ProjectId = projectA.Id, FileName = "Project-brief.txt",
                StoredPath = storedPath, ContentType = "text/plain", Size = sampleContent.Length, UploadedById = admin.Id
            });
            await using var secondSampleStream = new MemoryStream(sampleContent);
            var secondStoredPath = await storage.SaveAsync(secondSampleStream, CancellationToken.None);
            db.ProjectFiles.Add(new ProjectFile
            {
                ClientId = seedClientB.Id, ProjectId = projectB.Id, FileName = "Advisory-notes.txt",
                StoredPath = secondStoredPath, ContentType = "text/plain", Size = sampleContent.Length, UploadedById = userB.Id
            });

            await db.SaveChangesAsync();
            return;
        }

        var missingDemoEmails = demoEmails
            .Where(email => !demoUsers.Any(user => string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (missingDemoEmails.Length == 0)
        {
            await db.SaveChangesAsync();
            return;
        }

        var clientA = await db.Clients.FirstOrDefaultAsync(client => client.CompanyName == "Northstar Studio") ?? new Client { CompanyName = "Northstar Studio", Notes = "Brand and digital design partner" };
        var clientB = await db.Clients.FirstOrDefaultAsync(client => client.CompanyName == "Juniper & Co.") ?? new Client { CompanyName = "Juniper & Co.", Notes = "Advisory client" };

        if (!db.Clients.Any(client => client.Id == clientA.Id))
            db.Clients.Add(clientA);
        if (!db.Clients.Any(client => client.Id == clientB.Id))
            db.Clients.Add(clientB);

        User CreateMissingUser(string email, string fullName, UserRole role, Guid? clientId)
        {
            var user = new User
            {
                Email = email,
                FullName = fullName,
                Role = role,
                ClientId = clientId,
                PasswordHash = string.Empty,
                IsActive = true
            };
            user.PasswordHash = hasher.HashPassword(user, DemoPassword);
            db.Users.Add(user);
            return user;
        }

        if (missingDemoEmails.Contains("admin@demo.com", StringComparer.OrdinalIgnoreCase))
            CreateMissingUser("admin@demo.com", "Alex Morgan", UserRole.Admin, null);
        if (missingDemoEmails.Contains("clienta@demo.com", StringComparer.OrdinalIgnoreCase))
            CreateMissingUser("clienta@demo.com", "Mia Chen", UserRole.Client, clientA.Id);
        if (missingDemoEmails.Contains("clientb@demo.com", StringComparer.OrdinalIgnoreCase))
            CreateMissingUser("clientb@demo.com", "Noah Patel", UserRole.Client, clientB.Id);

        await db.SaveChangesAsync();
    }
}
