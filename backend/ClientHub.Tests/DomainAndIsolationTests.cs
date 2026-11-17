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
using System.Security.Claims;

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
                new ActivityLog { ClientId = clientB.Id, ProjectId = projectB.Id, ActorId = Guid.NewGuid(), Action = "B", Metadata = "" });
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
        Assert.False(await tenant.Projects.AnyAsync(x => x.Id == projectB.Id));
        Assert.False(await tenant.Invoices.AnyAsync(x => x.Id == invoiceB.Id));

        await using var missingClientScope = new ClientHubDbContext(options, new TestScope(null, userA, true));
        Assert.Empty(await missingClientScope.Users.ToListAsync());
        Assert.Empty(await missingClientScope.Clients.ToListAsync());
        Assert.Empty(await missingClientScope.Invitations.ToListAsync());
        Assert.Empty(await missingClientScope.Projects.ToListAsync());
        Assert.Empty(await missingClientScope.NotificationPreferences.ToListAsync());
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
            }

            Assert.Equal(unrelatedHash, users["other@example.com"].PasswordHash);
        }
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

    private static void SetAuthenticatedUser(ControllerBase controller, Guid userId)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test");
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
}
