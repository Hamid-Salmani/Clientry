using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClientHub.Domain;
using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClientHub.Tests;

public sealed class RouteAuthorizationTests(RouteAuthorizationTests.ApiFactory factory)
    : IClassFixture<RouteAuthorizationTests.ApiFactory>
{
    [Fact]
    public async Task EveryProtectedApiRouteRejectsAnonymousRequests()
    {
        using var client = factory.CreateClient();
        var routes = GetRoutes(factory).Where(route => route.IsProtected).ToArray();

        Assert.True(routes.Length >= 50, $"Expected a full API route inventory, found {routes.Length} protected routes.");
        foreach (var route in routes)
        {
            using var response = await client.SendAsync(new HttpRequestMessage(route.Method, route.Path));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task EveryAdminApiRouteRejectsClientOwnersAndMembers()
    {
        using var client = factory.CreateClient();
        var ownerToken = await Login(client, "clienta@demo.com");
        var memberToken = await Login(client, "clienta.member@demo.com");
        var routes = GetRoutes(factory).Where(route => route.IsAdminOnly).ToArray();

        Assert.NotEmpty(routes);
        foreach (var route in routes)
        {
            foreach (var token in new[] { ownerToken, memberToken })
            {
                using var request = new HttpRequestMessage(route.Method, route.Path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task ClientAReceivesNoClientBDataAcrossTenantScopedRoutes()
    {
        Guid clientBId;
        Guid projectBId;
        Guid fileBId;
        Guid invoiceBId;
        Guid requestBId;
        Guid milestoneBId;
        Guid notificationBId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClientHubDbContext>();
            var clientB = await db.Clients.IgnoreQueryFilters()
                .SingleAsync(client => client.CompanyName == "Juniper & Co.");
            var projectB = await db.Projects.IgnoreQueryFilters()
                .SingleAsync(project => project.ClientId == clientB.Id);
            var userB = await db.Users.IgnoreQueryFilters()
                .SingleAsync(user => user.Email == "clientb@demo.com");
            clientBId = clientB.Id;
            projectBId = projectB.Id;
            fileBId = await db.ProjectFiles.IgnoreQueryFilters()
                .Where(file => file.ProjectId == projectB.Id).Select(file => file.Id).SingleAsync();
            invoiceBId = await db.Invoices.IgnoreQueryFilters()
                .Where(invoice => invoice.ClientId == clientB.Id).Select(invoice => invoice.Id).SingleAsync();
            requestBId = await db.ProjectRequests.IgnoreQueryFilters()
                .Where(request => request.ClientId == clientB.Id).Select(request => request.Id).SingleAsync();

            var pendingMilestone = new Milestone
            {
                ClientId = clientB.Id,
                ProjectId = projectB.Id,
                Title = "Client B approval isolation check",
                Order = 3,
                ApprovalStatus = MilestoneApprovalStatus.Pending,
                RequestedAt = DateTime.UtcNow
            };
            var notification = new Notification
            {
                UserId = userB.Id,
                Type = "Isolation test",
                Message = "Client B notification isolation check.",
                Link = $"/projects/{projectB.Id}"
            };
            db.AddRange(pendingMilestone, notification);
            await db.SaveChangesAsync();
            milestoneBId = pendingMilestone.Id;
            notificationBId = notification.Id;
        }

        using var client = factory.CreateClient();
        var token = await Login(client, "clienta@demo.com");
        var isolatedGets = new[]
        {
            $"/api/projects/{projectBId}",
            $"/api/projects/{projectBId}/activity",
            $"/api/projects/{projectBId}/comments",
            $"/api/projects/{projectBId}/files",
            $"/api/projects/{projectBId}/files.zip",
            $"/api/projects/{projectBId}/files/{fileBId}",
            $"/api/projects/{projectBId}/files/{fileBId}/preview",
            $"/api/invoices/{invoiceBId}",
            $"/api/invoices/{invoiceBId}/pdf",
            $"/api/invoices/{invoiceBId}/receipt.pdf",
            $"/api/requests/{requestBId}",
            $"/api/requests/{requestBId}/attachments/{Guid.NewGuid()}",
            $"/api/projects/{projectBId}/milestones/{milestoneBId}/approval-decision",
            $"/api/notifications/{notificationBId}/read"
        };

        foreach (var path in isolatedGets)
        {
            var method = path.EndsWith("/approval-decision", StringComparison.Ordinal)
                ? HttpMethod.Post
                : path.EndsWith("/read", StringComparison.Ordinal) ? HttpMethod.Patch : HttpMethod.Get;
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (method == HttpMethod.Post)
                request.Content = JsonContent.Create(new { decision = "Approved", note = (string?)null });
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        foreach (var path in new[] { "/api/projects", "/api/approvals", "/api/requests", "/api/invoices", "/api/notifications" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(projectBId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(invoiceBId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(requestBId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(milestoneBId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(notificationBId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(clientBId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var path in new[] { "/api/me/team", "/api/me/billing" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("clientb@demo.com", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Juniper & Co.", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<string> Login(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email, password = "DemoPass!234" });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static IEnumerable<ApiRoute> GetRoutes(WebApplicationFactory<Program> application)
    {
        var descriptions = application.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.SelectMany(group => group.Items);
        var routes = descriptions
            .Where(description => description.HttpMethod is not null &&
                description.RelativePath?.StartsWith("api/", StringComparison.OrdinalIgnoreCase) == true)
            .Select(description =>
            {
                var metadata = description.ActionDescriptor.EndpointMetadata ?? [];
                var allowsAnonymous = metadata.OfType<IAllowAnonymous>().Any();
                var authorization = metadata.OfType<IAuthorizeData>().ToArray();
                var path = "/" + Regex.Replace(description.RelativePath!, @"\{[^}]+\}", Guid.Empty.ToString("D"));
                return new ApiRoute(
                    new HttpMethod(description.HttpMethod!),
                    path,
                    !allowsAnonymous && authorization.Length > 0,
                    !allowsAnonymous && authorization.Any(attribute =>
                        attribute.Roles?.Split(',').Any(role => role.Trim() == "Admin") == true));
            })
            .DistinctBy(route => (route.Method.Method, route.Path));
        return routes;
    }

    private sealed record ApiRoute(HttpMethod Method, string Path, bool IsProtected, bool IsAdminOnly);

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"clienthub-routes-{Guid.NewGuid():N}");
        private readonly Dictionary<string, string?> _originalEnvironment = [];

        public ApiFactory()
        {
            Directory.CreateDirectory(_root);
            SetEnvironment("ConnectionStrings__ClientHub", $"Data Source={Path.Combine(_root, "clienthub.db")}");
            SetEnvironment("Storage__Path", Path.Combine(_root, "files"));
            SetEnvironment("Jwt__SigningKey", "clienthub-route-test-signing-key-with-at-least-32-bytes");
            SetEnvironment("Smtp__Host", "127.0.0.1");
            SetEnvironment("Smtp__Port", "1025");
            SetEnvironment("Frontend__BaseUrl", "http://localhost:5173");
            SetEnvironment("Auth__AllowSelfRegistration", "false");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            foreach (var (key, value) in _originalEnvironment)
                Environment.SetEnvironmentVariable(key, value);
        }

        private void SetEnvironment(string key, string value)
        {
            _originalEnvironment[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
