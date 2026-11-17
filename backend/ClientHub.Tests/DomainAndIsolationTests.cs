using ClientHub.Application;
using ClientHub.Api.Controllers;
using ClientHub.Api.Services;
using ClientHub.Domain;
using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;

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

    [Fact]
    public void InvoiceTotalIncludesTaxAndDiscount()
    {
        var invoice = new Invoice
        {
            Number = "INV-2", Currency = "USD", DiscountAmount = 20, TaxAmount = 15,
            IssueDate = new DateOnly(2026, 11, 1), DueDate = new DateOnly(2026, 11, 30),
            Lines = [new InvoiceLineItem { Description = "Design", Quantity = 2, UnitPrice = 100 }]
        };
        Assert.Equal(195m, invoice.Total);
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft, InvoiceStatus.Sent, true)]
    [InlineData(InvoiceStatus.Draft, InvoiceStatus.Paid, false)]
    [InlineData(InvoiceStatus.Sent, InvoiceStatus.Paid, true)]
    [InlineData(InvoiceStatus.Paid, InvoiceStatus.Void, false)]
    public void InvoiceTransitionsFollowBusinessRules(InvoiceStatus current, InvoiceStatus next, bool expected) =>
        Assert.Equal(expected, InvoiceRules.CanTransition(current, next));

    [Theory]
    [InlineData(ProjectStatus.Pending, ProjectStatus.InProgress, true)]
    [InlineData(ProjectStatus.InProgress, ProjectStatus.Pending, true)]
    [InlineData(ProjectStatus.InProgress, ProjectStatus.Delivered, false)]
    [InlineData(ProjectStatus.InReview, ProjectStatus.Delivered, true)]
    [InlineData(ProjectStatus.Delivered, ProjectStatus.InReview, true)]
    [InlineData(ProjectStatus.Archived, ProjectStatus.Delivered, true)]
    [InlineData(ProjectStatus.Archived, ProjectStatus.InProgress, false)]
    public void ProjectTransitionsFollowAllowedForwardAndBackSteps(ProjectStatus current, ProjectStatus next, bool expected) =>
        Assert.Equal(expected, ProjectRules.CanTransition(current, next));

    [Theory]
    [InlineData(MilestoneApprovalStatus.NotRequired, false, true)]
    [InlineData(MilestoneApprovalStatus.Pending, false, false)]
    [InlineData(MilestoneApprovalStatus.Approved, false, true)]
    [InlineData(MilestoneApprovalStatus.NotRequired, true, false)]
    public void ApprovalCanOnlyBeRequestedForIncompleteNonPendingMilestones(
        MilestoneApprovalStatus status, bool completed, bool expected) =>
        Assert.Equal(expected, MilestoneApprovalRules.CanRequest(status, completed));

    [Theory]
    [InlineData(MilestoneApprovalStatus.Pending, true)]
    [InlineData(MilestoneApprovalStatus.Approved, false)]
    [InlineData(MilestoneApprovalStatus.ChangesRequested, false)]
    [InlineData(MilestoneApprovalStatus.NotRequired, false)]
    public void ApprovalCanOnlyBeDecidedWhilePending(MilestoneApprovalStatus status, bool expected) =>
        Assert.Equal(expected, MilestoneApprovalRules.CanDecide(status));

    [Fact]
    public async Task ApprovalRequestAndDecisionNotifyBothSidesAndCompleteApprovedMilestone()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var client = new Client { CompanyName = "Approval Client", Notes = "" };
        var admin = new User { Email = "admin@example.test", FullName = "Admin", Role = UserRole.Admin, PasswordHash = "" };
        var member = new User { Email = "member@example.test", FullName = "Client Member", Role = UserRole.Client, ClientId = client.Id, PasswordHash = "" };
        var project = new Project { ClientId = client.Id, Name = "Approval Project", Description = "", StartDate = new DateOnly(2026, 1, 1) };
        var milestone = new Milestone { ClientId = client.Id, ProjectId = project.Id, Title = "Design review", Order = 1 };
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        var emails = new CapturingEmailSender();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Frontend:BaseUrl"] = "https://portal.example.test"
        }).Build();

        await using (var adminDb = new ClientHubDbContext(options, new TestScope(null, admin.Id, false)))
        {
            await adminDb.Database.EnsureCreatedAsync();
            adminDb.AddRange(client, admin, member, project, milestone);
            await adminDb.SaveChangesAsync();
            var controller = new ApprovalsController(adminDb, emails, configuration);
            SetAuthenticatedUser(controller, admin.Id, UserRole.Admin);

            Assert.IsType<NoContentResult>(await controller.RequestApproval(project.Id, milestone.Id, CancellationToken.None));
            Assert.Equal(MilestoneApprovalStatus.Pending,
                (await adminDb.Milestones.SingleAsync(item => item.Id == milestone.Id)).ApprovalStatus);
            Assert.Single(await adminDb.Notifications.Where(item => item.UserId == member.Id).ToListAsync());
        }

        await using (var clientDb = new ClientHubDbContext(options, new TestScope(client.Id, member.Id, true)))
        {
            var controller = new ApprovalsController(clientDb, emails, configuration);
            SetAuthenticatedUser(controller, member.Id, UserRole.Client, client.Id);
            Assert.IsType<NoContentResult>(await controller.Decide(project.Id, milestone.Id,
                new ApprovalsController.ApprovalDecisionRequest("Approved", null), CancellationToken.None));
            var decided = await clientDb.Milestones.SingleAsync(item => item.Id == milestone.Id);
            Assert.Equal(MilestoneApprovalStatus.Approved, decided.ApprovalStatus);
            Assert.True(decided.IsCompleted);
            Assert.Equal(member.Id, decided.DecidedById);
            Assert.Contains(await clientDb.ActivityLogs.Select(item => item.Action).ToListAsync(), action => action == "Milestone approved");
            Assert.Single(await clientDb.Notifications.IgnoreQueryFilters().Where(item => item.UserId == admin.Id).ToListAsync());
        }

        Assert.Equal(2, emails.Messages.Count);
    }

    [Fact]
    public async Task ChangesRequestRequiresNoteAndARepeatedDecisionReturnsConflict()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var client = new Client { CompanyName = "Approval Client", Notes = "" };
        var member = new User { Email = "member@example.test", FullName = "Client Member", Role = UserRole.Client, ClientId = client.Id, PasswordHash = "" };
        var project = new Project { ClientId = client.Id, Name = "Approval Project", Description = "", StartDate = new DateOnly(2026, 1, 1) };
        var milestone = new Milestone
        {
            ClientId = client.Id, ProjectId = project.Id, Title = "Design review", Order = 1,
            ApprovalStatus = MilestoneApprovalStatus.Pending
        };
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        await using var db = new ClientHubDbContext(options, new TestScope(client.Id, member.Id, true));
        await db.Database.EnsureCreatedAsync();
        db.AddRange(client, member, project, milestone);
        await db.SaveChangesAsync();
        var controller = new ApprovalsController(db, new CapturingEmailSender(), new ConfigurationBuilder().Build());
        SetAuthenticatedUser(controller, member.Id, UserRole.Client, client.Id);

        Assert.IsType<BadRequestObjectResult>(await controller.Decide(project.Id, milestone.Id,
            new ApprovalsController.ApprovalDecisionRequest("ChangesRequested", "  "), CancellationToken.None));
        milestone.ApprovalStatus = MilestoneApprovalStatus.Approved;
        await db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await controller.Decide(project.Id, milestone.Id,
            new ApprovalsController.ApprovalDecisionRequest("Approved", null), CancellationToken.None));
    }

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
                new User { ClientId = null, Email = "admin@example.test", FullName = "Admin", Role = UserRole.Admin, PasswordHash = "hash" },
                new Invitation { Email = "invite-a@example.test", FullName = "Invite A", TokenHash = "hash-a", ClientId = clientA.Id, ExpiresAt = DateTime.UtcNow.AddDays(1) },
                new Invitation { Email = "invite-b@example.test", FullName = "Invite B", TokenHash = "hash-b", ClientId = clientB.Id, ExpiresAt = DateTime.UtcNow.AddDays(1) },
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
                new UserNotificationPreference { UserId = userA, WorkspaceUpdates = false, InvoiceReminders = true },
                new UserNotificationPreference { UserId = userB, WorkspaceUpdates = true, InvoiceReminders = false },
                new ActivityLog { ClientId = clientA.Id, ProjectId = projectA.Id, ActorId = userA, Action = "A", Metadata = "" },
                new ActivityLog { ClientId = clientB.Id, ProjectId = projectB.Id, ActorId = Guid.NewGuid(), Action = "B", Metadata = "" },
                new ProjectRequest
                {
                    ClientId = clientA.Id, CreatedById = userA, Title = "A request", Description = "A request description",
                    Attachments = [new ProjectRequestAttachment { ClientId = clientA.Id, FileName = "a.pdf", StoredPath = "a", ContentType = "application/pdf", Size = 1 }]
                },
                new ProjectRequest
                {
                    ClientId = clientB.Id, CreatedById = userB, Title = "B request", Description = "B request description",
                    Attachments = [new ProjectRequestAttachment { ClientId = clientB.Id, FileName = "b.pdf", StoredPath = "b", ContentType = "application/pdf", Size = 1 }]
                });
            await seed.SaveChangesAsync();
        }

        await using var tenant = new ClientHubDbContext(options, new TestScope(clientA.Id, userA, true));
        Assert.Equal(["a@example.test"], await tenant.Users.Select(x => x.Email).ToListAsync());
        Assert.Equal(["A"], await tenant.Clients.Select(x => x.CompanyName).ToListAsync());
        Assert.Equal(["invite-a@example.test"], await tenant.Invitations.Select(x => x.Email).ToListAsync());
        Assert.Equal(["A project"], await tenant.Projects.Select(x => x.Name).ToListAsync());
        Assert.Equal(["A milestone"], await tenant.Milestones.Select(x => x.Title).ToListAsync());
        Assert.Equal(["a.pdf"], await tenant.ProjectFiles.OrderByDescending(x => x.UploadedAt).Select(x => x.FileName).ToListAsync());
        Assert.Equal(["A comment"], await tenant.Comments.OrderBy(x => x.CreatedAt).Select(x => x.Body).ToListAsync());
        Assert.Equal(["A-1"], await tenant.Invoices.Select(x => x.Number).ToListAsync());
        Assert.Equal(["Work A"], await tenant.InvoiceLineItems.Select(x => x.Description).ToListAsync());
        Assert.Equal(["payment-a"], await tenant.Payments.Select(x => x.StripePaymentIntentId).ToListAsync());
        Assert.Equal(["A notification"], await tenant.Notifications.Select(x => x.Message).ToListAsync());
        Assert.Equal([userA], await tenant.NotificationPreferences.Select(x => x.UserId).ToListAsync());
        Assert.Equal(["A"], await tenant.ActivityLogs.Select(x => x.Action).ToListAsync());
        Assert.Equal(["A request"], await tenant.ProjectRequests.Select(x => x.Title).ToListAsync());
        Assert.Equal(["a.pdf"], await tenant.ProjectRequestAttachments.Select(x => x.FileName).ToListAsync());
        Assert.False(await tenant.Projects.AnyAsync(x => x.Id == projectB.Id));
        Assert.False(await tenant.Invoices.AnyAsync(x => x.Id == invoiceB.Id));

        await using var missingClientScope = new ClientHubDbContext(options, new TestScope(null, userA, true));
        Assert.Empty(await missingClientScope.Users.ToListAsync());
        Assert.Empty(await missingClientScope.Clients.ToListAsync());
        Assert.Empty(await missingClientScope.Invitations.ToListAsync());
        Assert.Empty(await missingClientScope.Projects.ToListAsync());
        Assert.Empty(await missingClientScope.NotificationPreferences.ToListAsync());
        Assert.Empty(await missingClientScope.ProjectRequests.ToListAsync());
        Assert.Empty(await missingClientScope.ProjectRequestAttachments.ToListAsync());
    }

    [Fact]
    public async Task ProjectRequestsEnforceStatusTransitionsNotesAndCancellationRules()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var client = new Client { CompanyName = "Request Client", Notes = "" };
        var clientUser = new User
        {
            Email = "request-client@example.test", FullName = "Client User", Role = UserRole.Client,
            ClientId = client.Id, PasswordHash = "hash"
        };
        var admin = new User
        {
            Email = "request-admin@example.test", FullName = "Admin", Role = UserRole.Admin,
            PasswordHash = "hash"
        };
        var newRequest = new ProjectRequest
        {
            ClientId = client.Id, CreatedById = clientUser.Id, Title = "New work",
            Description = "New work description"
        };
        var cancellableRequest = new ProjectRequest
        {
            ClientId = client.Id, CreatedById = clientUser.Id, Title = "Cancellable",
            Description = "Cancellable description"
        };
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        await using var db = new ClientHubDbContext(options, new TestScope(null, admin.Id, false));
        await db.Database.EnsureCreatedAsync();
        db.AddRange(client, clientUser, admin, newRequest, cancellableRequest);
        await db.SaveChangesAsync();

        var controller = new ProjectRequestsController(db, new UnusedFileStorage(),
            new ClientHub.Api.Validators.ProjectRequestValidator());
        SetAuthenticatedUser(controller, admin.Id, UserRole.Admin);
        Assert.IsType<NoContentResult>(await controller.StartReview(newRequest.Id, CancellationToken.None));
        Assert.Equal(ProjectRequestStatus.InReview, newRequest.Status);
        Assert.Single(await db.Notifications.Where(notification => notification.UserId == clientUser.Id).ToListAsync());
        Assert.IsType<BadRequestObjectResult>(await controller.Decide(newRequest.Id,
            new ProjectRequestsController.AdminDecisionRequest("Accepted", " "), CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.Decide(newRequest.Id,
            new ProjectRequestsController.AdminDecisionRequest("Accepted", "We can begin next week."), CancellationToken.None));
        Assert.Equal(ProjectRequestStatus.Accepted, newRequest.Status);
        Assert.IsType<ConflictObjectResult>(await controller.Decide(newRequest.Id,
            new ProjectRequestsController.AdminDecisionRequest("Declined", "No longer available."), CancellationToken.None));

        SetAuthenticatedUser(controller, clientUser.Id, UserRole.Client, client.Id);
        Assert.IsType<NoContentResult>(await controller.Cancel(cancellableRequest.Id, CancellationToken.None));
        Assert.Equal(ProjectRequestStatus.Cancelled, cancellableRequest.Status);
        Assert.IsType<ConflictObjectResult>(await controller.Cancel(newRequest.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ClientOverviewIsTenantScopedAndTracksActivitySinceLastVisit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var client = new Client { CompanyName = "Overview Client", Notes = "" };
        var clientUser = new User
        {
            Email = "overview-client@example.test", FullName = "Client User", Role = UserRole.Client,
            ClientId = client.Id, PasswordHash = "hash"
        };
        var project = new Project
        {
            ClientId = client.Id, Name = "Overview project", Description = "",
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        var invoice = new Invoice
        {
            ClientId = client.Id, Number = "OV-1", Currency = "USD", Status = InvoiceStatus.Overdue,
            IssueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10)),
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            Lines = [new InvoiceLineItem { Description = "Design", Quantity = 2, UnitPrice = 75 }]
        };
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        await using var seed = new ClientHubDbContext(options, new TestScope(null, null, false));
        await seed.Database.EnsureCreatedAsync();
        seed.AddRange(client, clientUser, project, invoice,
            new Milestone
            {
                ClientId = client.Id, ProjectId = project.Id, Title = "Client review",
                ApprovalStatus = MilestoneApprovalStatus.Pending, DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2))
            },
            new Milestone
            {
                ClientId = client.Id, ProjectId = project.Id, Title = "Later milestone",
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5))
            },
            new Notification { UserId = clientUser.Id, Type = "Invoice", Message = "Invoice is overdue" },
            new ActivityLog
            {
                ClientId = client.Id, ProjectId = project.Id, ActorId = clientUser.Id,
                Action = "File uploaded", Metadata = "brief.pdf", CreatedAt = DateTime.UtcNow.AddMinutes(-1)
            });
        await seed.SaveChangesAsync();

        await using var db = new ClientHubDbContext(options, new TestScope(client.Id, clientUser.Id, true));
        var controller = new ClientOverviewController(db);
        SetAuthenticatedUser(controller, clientUser.Id, UserRole.Client, client.Id);
        var firstResponse = Assert.IsType<OkObjectResult>(await controller.Overview(CancellationToken.None));
        using var first = JsonDocument.Parse(JsonSerializer.Serialize(firstResponse.Value));
        Assert.Single(first.RootElement.GetProperty("pendingApprovals").EnumerateArray());
        var unpaid = first.RootElement.GetProperty("unpaidInvoices").EnumerateArray().Single();
        Assert.Equal(150m, unpaid.GetProperty("amount").GetDecimal());
        Assert.Equal(1, first.RootElement.GetProperty("unreadNotificationCount").GetInt32());
        Assert.Single(first.RootElement.GetProperty("recentProjectActivity").EnumerateArray());
        Assert.Single(first.RootElement.GetProperty("nextMilestones").EnumerateArray());
        Assert.NotNull(await db.Users.Where(user => user.Id == clientUser.Id).Select(user => user.LastVisitedAt).SingleAsync());

        var secondResponse = Assert.IsType<OkObjectResult>(await controller.Overview(CancellationToken.None));
        using var second = JsonDocument.Parse(JsonSerializer.Serialize(secondResponse.Value));
        Assert.Empty(second.RootElement.GetProperty("recentProjectActivity").EnumerateArray());
    }

    [Fact]
    public async Task ProjectFilePreviewAndArchiveAreScopedAndSizeCapped()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clientA = new Client { CompanyName = "File Client A", Notes = "" };
        var clientB = new Client { CompanyName = "File Client B", Notes = "" };
        var userA = new User { Email = "file-a@example.test", FullName = "A", Role = UserRole.Client, ClientId = clientA.Id, PasswordHash = "hash" };
        var userB = new User { Email = "file-b@example.test", FullName = "B", Role = UserRole.Client, ClientId = clientB.Id, PasswordHash = "hash" };
        var projectA = new Project { ClientId = clientA.Id, Name = "A", Description = "", StartDate = DateOnly.FromDateTime(DateTime.UtcNow) };
        var projectB = new Project { ClientId = clientB.Id, Name = "B", Description = "", StartDate = DateOnly.FromDateTime(DateTime.UtcNow) };
        var oversizedProject = new Project { ClientId = clientA.Id, Name = "Large", Description = "", StartDate = DateOnly.FromDateTime(DateTime.UtcNow) };
        var image = new ProjectFile
        {
            ClientId = clientA.Id, ProjectId = projectA.Id, FileName = "preview.png",
            StoredPath = "preview", ContentType = "image/png", Size = 4, UploadedById = userA.Id
        };
        var foreignFile = new ProjectFile
        {
            ClientId = clientB.Id, ProjectId = projectB.Id, FileName = "secret.txt",
            StoredPath = "foreign", ContentType = "text/plain", Size = 6, UploadedById = userB.Id
        };
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        await using (var seed = new ClientHubDbContext(options, new TestScope(null, null, false)))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.AddRange(clientA, clientB, userA, userB, projectA, projectB, oversizedProject, image, foreignFile,
                new ProjectFile
                {
                    ClientId = clientA.Id, ProjectId = oversizedProject.Id, FileName = "large.bin",
                    StoredPath = "large", ContentType = "text/plain", Size = 51 * 1024 * 1024, UploadedById = userA.Id
                });
            await seed.SaveChangesAsync();
        }

        var storage = new MemoryFileStorage(new Dictionary<string, byte[]>
        {
            ["preview"] = [1, 2, 3, 4], ["foreign"] = [5, 6, 7, 8, 9, 10], ["large"] = [1]
        });
        await using var db = new ClientHubDbContext(options, new TestScope(clientA.Id, userA.Id, true));
        var controller = new FilesController(db, storage);
        SetAuthenticatedUser(controller, userA.Id, UserRole.Client, clientA.Id);
        var zip = Assert.IsType<FileStreamResult>(await controller.DownloadZip(projectA.Id, CancellationToken.None));
        using (var archive = new ZipArchive(zip.FileStream, ZipArchiveMode.Read))
        {
            Assert.Equal(["preview.png"], archive.Entries.Select(entry => entry.FullName));
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("secret", StringComparison.OrdinalIgnoreCase));
        }
        Assert.IsType<NotFoundResult>(await controller.DownloadZip(projectB.Id, CancellationToken.None));
        var oversized = Assert.IsType<ObjectResult>(await controller.DownloadZip(oversizedProject.Id, CancellationToken.None));
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, oversized.StatusCode);

        var preview = Assert.IsType<FileStreamResult>(await controller.Preview(projectA.Id, image.Id, CancellationToken.None));
        Assert.Equal("image/png", preview.ContentType);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"]);
        var textFile = new ProjectFile
        {
            ClientId = clientA.Id, ProjectId = projectA.Id, FileName = "notes.txt",
            StoredPath = "text", ContentType = "text/plain", Size = 1, UploadedById = userA.Id
        };
        db.ProjectFiles.Add(textFile);
        await db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await controller.Preview(projectA.Id, textFile.Id, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Preview(projectB.Id, foreignFile.Id, CancellationToken.None));
    }

    [Fact]
    public async Task InvoiceDetailsAndReceiptsRespectClientScopeAndPaidStatus()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clientA = new Client { CompanyName = "Invoice Client A", Notes = "" };
        var clientB = new Client { CompanyName = "Invoice Client B", Notes = "" };
        var userA = new User { Email = "invoice-a@example.test", FullName = "A", Role = UserRole.Client, ClientId = clientA.Id, PasswordHash = "hash" };
        var invoiceA = new Invoice
        {
            ClientId = clientA.Id, Number = "PAID-A", Currency = "USD", Status = InvoiceStatus.Paid,
            IssueDate = new DateOnly(2026, 11, 1), DueDate = new DateOnly(2026, 11, 15),
            Lines = [new InvoiceLineItem { Description = "Design", Quantity = 1, UnitPrice = 100 }]
        };
        var invoiceB = new Invoice
        {
            ClientId = clientB.Id, Number = "PAID-B", Currency = "USD", Status = InvoiceStatus.Paid,
            IssueDate = new DateOnly(2026, 11, 1), DueDate = new DateOnly(2026, 11, 15),
            Lines = [new InvoiceLineItem { Description = "Private work", Quantity = 1, UnitPrice = 200 }]
        };
        var unpaid = new Invoice
        {
            ClientId = clientA.Id, Number = "UNPAID-A", Currency = "USD", Status = InvoiceStatus.Sent,
            IssueDate = new DateOnly(2026, 11, 1), DueDate = new DateOnly(2026, 11, 15),
            Lines = [new InvoiceLineItem { Description = "Work", Quantity = 1, UnitPrice = 50 }]
        };
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        await using (var seed = new ClientHubDbContext(options, new TestScope(null, null, false)))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.AddRange(clientA, clientB, userA, invoiceA, invoiceB, unpaid,
                new Payment
                {
                    InvoiceId = invoiceA.Id, StripePaymentIntentId = "receipt-payment-a",
                    Amount = 100, Status = "Succeeded", PaidAt = DateTime.UtcNow
                },
                new Payment
                {
                    InvoiceId = invoiceB.Id, StripePaymentIntentId = "receipt-payment-b",
                    Amount = 200, Status = "Succeeded", PaidAt = DateTime.UtcNow
                });
            await seed.SaveChangesAsync();
        }

        await using var db = new ClientHubDbContext(options, new TestScope(clientA.Id, userA.Id, true));
        var controller = new InvoicesController(db, new InvoicePdfService());
        SetAuthenticatedUser(controller, userA.Id, UserRole.Client, clientA.Id);
        var detail = Assert.IsType<OkObjectResult>(await controller.Get(invoiceA.Id, CancellationToken.None));
        using var detailJson = JsonDocument.Parse(JsonSerializer.Serialize(detail.Value));
        Assert.Equal("PAID-A", detailJson.RootElement.GetProperty("Number").GetString());
        Assert.Equal(100m, detailJson.RootElement.GetProperty("lineSubtotal").GetDecimal());
        Assert.Single(detailJson.RootElement.GetProperty("payments").EnumerateArray());
        Assert.IsType<NotFoundResult>(await controller.Get(invoiceB.Id, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Receipt(unpaid.Id, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Receipt(invoiceB.Id, CancellationToken.None));
        var receipt = Assert.IsType<FileContentResult>(await controller.Receipt(invoiceA.Id, CancellationToken.None));
        Assert.Equal("application/pdf", receipt.ContentType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(receipt.FileContents));
    }

    [Fact]
    public async Task OnlyClientOwnerCanUpdateBillingDetails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var client = new Client { CompanyName = "Billing Client", Notes = "" };
        var owner = new User
        {
            Email = "billing-owner@example.test", FullName = "Owner", Role = UserRole.Client,
            ClientRole = ClientRole.Owner, ClientId = client.Id, PasswordHash = "hash"
        };
        var member = new User
        {
            Email = "billing-member@example.test", FullName = "Member", Role = UserRole.Client,
            ClientRole = ClientRole.Member, ClientId = client.Id, PasswordHash = "hash"
        };
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        await using var db = new ClientHubDbContext(options, new TestScope(client.Id, owner.Id, true));
        await db.Database.EnsureCreatedAsync();
        db.AddRange(client, owner, member);
        await db.SaveChangesAsync();
        var controller = new ClientBillingController(db);
        var request = new ClientBillingController.BillingDetailsRequest(
            "Billing Client LLC", "TAX-1", "billing@example.test; finance@example.test", "1 Main Street");

        SetAuthenticatedUser(controller, member.Id, UserRole.Client, client.Id);
        Assert.IsType<OkObjectResult>(await controller.Get(CancellationToken.None));
        Assert.IsType<ForbidResult>(await controller.Update(request, CancellationToken.None));

        SetAuthenticatedUser(controller, owner.Id, UserRole.Client, client.Id);
        Assert.IsType<NoContentResult>(await controller.Update(request, CancellationToken.None));
        var savedClient = await db.Clients.SingleAsync();
        Assert.Equal("Billing Client LLC", savedClient.LegalName);
        Assert.Equal("TAX-1", savedClient.TaxId);
        Assert.Equal("billing@example.test; finance@example.test", savedClient.BillingEmails);
    }

    [Fact]
    public async Task DemoInitializationRepairsExistingClientCredentialsAndUnlocksThem()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var services = new ServiceCollection()
            .AddDbContext<ClientHubDbContext>(options => options.UseSqlite(connection))
            .AddScoped<IRequestScope>(_ => new TestScope(null, null, false))
            .AddScoped<IFileStorage, UnusedFileStorage>()
            .BuildServiceProvider();

        var hasher = new PasswordHasher<User>();
        var userA = new User
        {
            Email = "clienta@demo.com", FullName = "Client A", Role = UserRole.Client,
            PasswordHash = "", FailedLoginAttempts = 2
        };
        userA.PasswordHash = hasher.HashPassword(userA, "old-password");
        var userB = new User
        {
            Email = "clientb@demo.com", FullName = "Client B", Role = UserRole.Client,
            PasswordHash = "", LockedUntil = DateTime.UtcNow.AddMinutes(10)
        };
        userB.PasswordHash = hasher.HashPassword(userB, "old-password");
        var unrelatedUser = new User
        {
            Email = "other@example.com", FullName = "Other User", Role = UserRole.Client,
            PasswordHash = ""
        };
        unrelatedUser.PasswordHash = hasher.HashPassword(unrelatedUser, "unchanged-password");
        var unrelatedHash = unrelatedUser.PasswordHash;

        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClientHubDbContext>();
            await db.Database.MigrateAsync();
            db.Users.AddRange(userA, userB, unrelatedUser);
            await db.SaveChangesAsync();
        }

        await DemoData.InitializeAsync(services);

        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClientHubDbContext>();
            var users = await db.Users.AsNoTracking().ToDictionaryAsync(user => user.Email);
            foreach (var email in new[] { "clienta@demo.com", "clientb@demo.com" })
            {
                var user = users[email];
                Assert.NotEqual(PasswordVerificationResult.Failed,
                    hasher.VerifyHashedPassword(user, user.PasswordHash, "DemoPass!234"));
                Assert.Equal(0, user.FailedLoginAttempts);
                Assert.Null(user.LockedUntil);
                Assert.Equal(ClientRole.Owner, user.ClientRole);
                Assert.NotNull(user.ClientId);
            }

            Assert.Equal(ClientRole.Member, users["clienta.member@demo.com"].ClientRole);
            Assert.Equal(ClientRole.Member, users["clientb.member@demo.com"].ClientRole);
            Assert.Equal(users["clienta@demo.com"].ClientId, users["clienta.member@demo.com"].ClientId);
            Assert.Equal(users["clientb@demo.com"].ClientId, users["clientb.member@demo.com"].ClientId);
            Assert.Equal(unrelatedHash, users["other@example.com"].PasswordHash);
        }
    }

    [Fact]
    public async Task CurrentUserProfileReturnsItsAssignedClientWorkspaceAndSupportContact()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var client = new Client { CompanyName = "Assigned Client", Notes = "" };
        var userId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        await using var db = new ClientHubDbContext(options, new TestScope(client.Id, userId, true));
        await db.Database.EnsureCreatedAsync();
        db.Clients.Add(client);
        db.WorkspaceSettings.Add(new WorkspaceSettings { Name = "Agency Workspace", SupportEmail = "help@example.test" });
        await db.SaveChangesAsync();

        var controller = CreateAuthController(db);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Client User"),
            new Claim(ClaimTypes.Role, "Client"),
            new Claim("client_id", client.Id.ToString())
        ], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        var result = Assert.IsType<OkObjectResult>(await controller.Me(CancellationToken.None));
        using var profile = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal("Assigned Client", profile.RootElement.GetProperty("workspaceName").GetString());
        Assert.Equal("help@example.test", profile.RootElement.GetProperty("supportEmail").GetString());
    }

    [Fact]
    public async Task InvitationAcceptanceCreatesClientUserOnceAndRejectsExpiredInvitations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        var client = new Client { CompanyName = "Invited Client", Notes = "" };
        var inviteToken = TokenSecurity.CreateToken();
        var expiredToken = TokenSecurity.CreateToken();

        await using (var seed = new ClientHubDbContext(options, new TestScope(null, null, false)))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.AddRange(client,
                new Invitation
                {
                    Email = "new-client@example.test", FullName = "New Client",
                    TokenHash = TokenSecurity.HashToken(inviteToken), ClientId = client.Id,
                    ExpiresAt = DateTime.UtcNow.AddDays(1)
                },
                new Invitation
                {
                    Email = "expired@example.test", FullName = "Expired Client",
                    TokenHash = TokenSecurity.HashToken(expiredToken), ClientId = client.Id,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
                });
            await seed.SaveChangesAsync();
        }

        await using var db = new ClientHubDbContext(options, new TestScope(null, null, false));
        var controller = CreateAuthController(db);
        Assert.IsType<OkObjectResult>(await controller.AcceptInvitation(
            inviteToken, new AuthController.AcceptInvitationRequest("SecurePass!234"), CancellationToken.None));

        var created = await db.Users.SingleAsync(user => user.Email == "new-client@example.test");
        Assert.Equal(client.Id, created.ClientId);
        Assert.Equal(UserRole.Client, created.Role);
        Assert.Equal(ClientRole.Owner, created.ClientRole);
        Assert.NotNull((await db.Invitations.SingleAsync(x => x.Email == created.Email)).AcceptedAt);
        Assert.IsType<NotFoundObjectResult>(await controller.AcceptInvitation(
            inviteToken, new AuthController.AcceptInvitationRequest("AnotherPass!234"), CancellationToken.None));
        Assert.IsType<NotFoundObjectResult>(await controller.AcceptInvitation(
            expiredToken, new AuthController.AcceptInvitationRequest("AnotherPass!234"), CancellationToken.None));
    }

    [Fact]
    public async Task RefreshTokenRotationDetectsReuseAndRevokesTheActiveReplacement()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        var user = new User
        {
            Email = "refresh@example.test", FullName = "Refresh User", Role = UserRole.Admin,
            PasswordHash = "hash"
        };
        var refreshToken = TokenSecurity.CreateToken();

        await using (var seed = new ClientHubDbContext(options, new TestScope(null, null, false)))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Users.Add(user);
            seed.RefreshTokens.Add(new RefreshToken
            {
                UserId = user.Id, TokenHash = TokenSecurity.HashToken(refreshToken),
                ExpiresAt = DateTime.UtcNow.AddDays(1)
            });
            await seed.SaveChangesAsync();
        }

        await using var db = new ClientHubDbContext(options, new TestScope(null, null, false));
        var controller = CreateAuthController(db);
        var rotated = Assert.IsType<OkObjectResult>(
            await controller.Refresh(new AuthController.RefreshRequest(refreshToken), CancellationToken.None));
        var response = Assert.IsType<AuthController.AuthResponse>(rotated.Value);
        Assert.NotEqual(refreshToken, response.RefreshToken);
        Assert.NotNull((await db.RefreshTokens.SingleAsync(x => x.TokenHash == TokenSecurity.HashToken(refreshToken))).RevokedAt);

        Assert.IsType<UnauthorizedObjectResult>(
            await controller.Refresh(new AuthController.RefreshRequest(refreshToken), CancellationToken.None));
        var replacement = await db.RefreshTokens.SingleAsync(x => x.TokenHash == TokenSecurity.HashToken(response.RefreshToken));
        Assert.NotNull(replacement.RevokedAt);
    }

    [Fact]
    public async Task LoginIsBlockedWhenTheOwningClientIsInactive()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        var client = new Client { CompanyName = "Inactive Client", Notes = "", IsActive = false };
        var user = new User
        {
            Email = "inactive@example.test", FullName = "Inactive User", Role = UserRole.Client,
            ClientId = client.Id, PasswordHash = ""
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "CorrectPass!234");

        await using (var seed = new ClientHubDbContext(options, new TestScope(null, null, false)))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.AddRange(client, user);
            await seed.SaveChangesAsync();
        }

        await using var db = new ClientHubDbContext(options, new TestScope(null, null, false));
        var result = await CreateAuthController(db).Login(
            new AuthController.LoginRequest(user.Email, "CorrectPass!234"), CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task AdminClientListAppliesSearchPaginationAndSorting()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        await using var db = new ClientHubDbContext(options, new TestScope(null, null, false));
        await db.Database.EnsureCreatedAsync();
        db.Clients.AddRange(
            new Client { CompanyName = "North Studio", Notes = "" },
            new Client { CompanyName = "North Creative", Notes = "" },
            new Client { CompanyName = "South Studio", Notes = "" });
        await db.SaveChangesAsync();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var controller = new AdminController(db, new UnusedEmailSender(), configuration);
        var result = Assert.IsType<OkObjectResult>(
            await controller.Clients("north", 1, 1, "companyName", false, CancellationToken.None));
        var page = Assert.IsType<PagedResult<object>>(result.Value);
        Assert.Equal(2, page.Total);
        Assert.Single(page.Items);
        Assert.Equal(1, page.Page);
        Assert.Equal(1, page.PageSize);
    }

    [Fact]
    public async Task InvoiceListFiltersAndReturnsPagedTotals()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        await using var db = new ClientHubDbContext(options, new TestScope(null, null, false));
        await db.Database.EnsureCreatedAsync();
        var client = new Client { CompanyName = "North Studio", Notes = "" };
        db.Clients.Add(client);
        db.Invoices.AddRange(
            new Invoice
            {
                ClientId = client.Id, Number = "INV-002", Currency = "USD", Status = InvoiceStatus.Sent,
                IssueDate = new DateOnly(2026, 2, 1), DueDate = new DateOnly(2026, 3, 1),
                Lines = [new InvoiceLineItem { Description = "Design", Quantity = 2, UnitPrice = 125 }]
            },
            new Invoice
            {
                ClientId = client.Id, Number = "INV-001", Currency = "USD", Status = InvoiceStatus.Sent,
                IssueDate = new DateOnly(2026, 1, 1), DueDate = new DateOnly(2026, 2, 1),
                Lines = [new InvoiceLineItem { Description = "Review", Quantity = 1, UnitPrice = 75 }]
            });
        await db.SaveChangesAsync();

        var controller = new InvoicesController(db, new InvoicePdfService());
        var result = Assert.IsType<OkObjectResult>(await controller.List(
            "north", "Sent", null, 1, 1, "number", false, CancellationToken.None));
        var page = Assert.IsType<PagedResult<object>>(result.Value);
        Assert.Equal(2, page.Total);
        Assert.Single(page.Items);
        Assert.Equal(1, page.Page);
        Assert.Equal(1, page.PageSize);
    }

    [Fact]
    public async Task NotificationsAreUserScopedAndPreferencesPersist()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        await using var db = new ClientHubDbContext(options, new TestScope(null, null, false));
        await db.Database.EnsureCreatedAsync();
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var ownNotification = new Notification { UserId = userId, Type = "Project", Message = "Project updated" };
        var privateNotification = new Notification { UserId = otherUserId, Type = "Invoice", Message = "Invoice ready" };
        db.Users.AddRange(
            new User { Id = userId, Email = "notify@example.test", FullName = "Notify User", Role = UserRole.Admin, PasswordHash = "hash" },
            new User { Id = otherUserId, Email = "other-notify@example.test", FullName = "Other User", Role = UserRole.Admin, PasswordHash = "hash" });
        db.Notifications.AddRange(ownNotification, privateNotification);
        await db.SaveChangesAsync();

        var controller = new NotificationsController(db);
        SetAuthenticatedUser(controller, userId);
        var listResult = Assert.IsType<OkObjectResult>(
            await controller.List(1, 1, null, CancellationToken.None));
        var page = Assert.IsType<NotificationsController.NotificationsPage>(listResult.Value);
        Assert.Equal(1, page.Total);
        Assert.Equal(1, page.UnreadCount);
        Assert.Single(page.Items);
        Assert.IsType<NotFoundResult>(await controller.MarkRead(privateNotification.Id, CancellationToken.None));
        Assert.False(await db.Notifications.Where(item => item.Id == privateNotification.Id).Select(item => item.IsRead).SingleAsync());

        Assert.IsType<NoContentResult>(await controller.MarkRead(ownNotification.Id, CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.UpdatePreferences(
            new NotificationsController.NotificationPreferencesRequest(false, true), CancellationToken.None));
        var preferences = await db.NotificationPreferences.SingleAsync(item => item.UserId == userId);
        Assert.False(preferences.WorkspaceUpdates);
        Assert.True(preferences.InvoiceReminders);
    }

    [Fact]
    public async Task WorkspaceSettingsRejectInvalidEmailAndPersistNormalizedValues()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        await using var db = new ClientHubDbContext(options, new TestScope(null, null, false));
        await db.Database.EnsureCreatedAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var controller = new AdminController(db, new UnusedEmailSender(), configuration);

        Assert.IsType<BadRequestObjectResult>(await controller.UpdateWorkspaceSettings(
            new AdminController.WorkspaceSettingsRequest("North Studio", "invalid address"), CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.UpdateWorkspaceSettings(
            new AdminController.WorkspaceSettingsRequest("  North Studio  ", "SUPPORT@EXAMPLE.COM"), CancellationToken.None));
        var saved = await db.WorkspaceSettings.SingleAsync();
        Assert.Equal("North Studio", saved.Name);
        Assert.Equal("support@example.com", saved.SupportEmail);
    }

    [Fact]
    public async Task ClientTeamIsOwnerManagedAndAdminCanReassignTheOwner()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var client = new Client { CompanyName = "Team Client", Notes = "" };
        var otherClient = new Client { CompanyName = "Other Client", Notes = "" };
        var owner = new User
        {
            Email = "owner@example.test", FullName = "Workspace Owner", Role = UserRole.Client,
            ClientRole = ClientRole.Owner, ClientId = client.Id, PasswordHash = ""
        };
        var member = new User
        {
            Email = "member@example.test", FullName = "Workspace Member", Role = UserRole.Client,
            ClientRole = ClientRole.Member, ClientId = client.Id, PasswordHash = ""
        };
        var outsider = new User
        {
            Email = "outsider@example.test", FullName = "Other Member", Role = UserRole.Client,
            ClientRole = ClientRole.Member, ClientId = otherClient.Id, PasswordHash = ""
        };
        var admin = new User { Email = "admin@example.test", FullName = "Admin", Role = UserRole.Admin, PasswordHash = "" };
        var options = new DbContextOptionsBuilder<ClientHubDbContext>().UseSqlite(connection).Options;
        await using (var seed = new ClientHubDbContext(options, new TestScope(null, null, false)))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.AddRange(client, otherClient, owner, member, outsider, admin);
            await seed.SaveChangesAsync();
        }

        var emails = new CapturingEmailSender();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Frontend:BaseUrl"] = "https://portal.example.test"
        }).Build();
        await using (var ownerDb = new ClientHubDbContext(options, new TestScope(client.Id, owner.Id, true)))
        {
            var controller = new ClientTeamController(ownerDb, emails, configuration);
            SetAuthenticatedUser(controller, owner.Id, UserRole.Client, client.Id);
            Assert.IsType<ConflictObjectResult>(await controller.Invite(
                new ClientTeamController.InvitationRequest(outsider.Email, "Other Member"), CancellationToken.None));
            var invitationResult = Assert.IsType<CreatedAtActionResult>(await controller.Invite(
                new ClientTeamController.InvitationRequest("new.person@example.test", "New Person"), CancellationToken.None));
            Assert.NotNull(invitationResult.Value);
            Assert.Single(emails.Messages);
            Assert.Equal(client.Id, (await ownerDb.Invitations.SingleAsync()).ClientId);
            Assert.IsType<NotFoundResult>(await controller.Deactivate(outsider.Id, CancellationToken.None));
        }

        await using (var memberDb = new ClientHubDbContext(options, new TestScope(client.Id, member.Id, true)))
        {
            var controller = new ClientTeamController(memberDb, emails, configuration);
            SetAuthenticatedUser(controller, member.Id, UserRole.Client, client.Id);
            Assert.IsType<ForbidResult>(await controller.List(CancellationToken.None));
            Assert.IsType<ForbidResult>(await controller.Invite(
                new ClientTeamController.InvitationRequest("blocked@example.test", "Blocked User"), CancellationToken.None));
            Assert.IsType<ForbidResult>(await controller.Deactivate(member.Id, CancellationToken.None));
        }

        await using (var adminDb = new ClientHubDbContext(options, new TestScope(null, admin.Id, false)))
        {
            var controller = new AdminController(adminDb, emails, configuration);
            SetAuthenticatedUser(controller, admin.Id, UserRole.Admin);
            Assert.IsType<NoContentResult>(await controller.SetClientOwner(client.Id,
                new AdminController.ClientOwnerRequest(member.Id), CancellationToken.None));
            Assert.Equal(ClientRole.Member, (await adminDb.Users.SingleAsync(user => user.Id == owner.Id)).ClientRole);
            Assert.Equal(ClientRole.Owner, (await adminDb.Users.SingleAsync(user => user.Id == member.Id)).ClientRole);
        }
    }

    private static void SetAuthenticatedUser(ControllerBase controller, Guid userId, UserRole? role = null, Guid? clientId = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (role.HasValue) claims.Add(new Claim(ClaimTypes.Role, role.Value.ToString()));
        if (clientId.HasValue) claims.Add(new Claim("client_id", clientId.Value.ToString()));
        var identity = new ClaimsIdentity(claims, "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private static AuthController CreateAuthController(ClientHubDbContext db)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "test-signing-key-with-at-least-32-bytes",
            ["Jwt:Issuer"] = "ClientHub",
            ["Jwt:Audience"] = "ClientHub"
        }).Build();
        return new AuthController(db, configuration, new TestEnvironment(), new UnusedEmailSender(),
            NullLogger<AuthController>.Instance);
    }

    private sealed class UnusedEmailSender : IEmailSender
    {
        public Task SendAsync(string recipient, string subject, string htmlBody,
            IReadOnlyDictionary<string, byte[]>? attachments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Email is not used by this test.");
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<(string Recipient, string Subject, string HtmlBody)> Messages { get; } = [];

        public Task SendAsync(string recipient, string subject, string htmlBody,
            IReadOnlyDictionary<string, byte[]>? attachments, CancellationToken cancellationToken)
        {
            Messages.Add((recipient, subject, htmlBody));
            return Task.CompletedTask;
        }
    }

    private sealed record TestScope(Guid? ClientId, Guid? UserId, bool IsClient) : IRequestScope;

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "ClientHub.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class UnusedFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(Stream content, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("File storage is not used by this test.");

        public Task<Stream> OpenReadAsync(string storedPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("File storage is not used by this test.");

        public Task DeleteAsync(string storedPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("File storage is not used by this test.");
    }

    private sealed class MemoryFileStorage(IReadOnlyDictionary<string, byte[]> files) : IFileStorage
    {
        public async Task<string> SaveAsync(Stream content, CancellationToken cancellationToken)
        {
            var path = Guid.NewGuid().ToString("N");
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            ((Dictionary<string, byte[]>)files)[path] = buffer.ToArray();
            return path;
        }

        public Task<Stream> OpenReadAsync(string storedPath, CancellationToken cancellationToken) =>
            files.TryGetValue(storedPath, out var content)
                ? Task.FromResult<Stream>(new MemoryStream(content, writable: false))
                : throw new FileNotFoundException();

        public Task DeleteAsync(string storedPath, CancellationToken cancellationToken)
        {
            ((Dictionary<string, byte[]>)files).Remove(storedPath);
            return Task.CompletedTask;
        }
    }
}
