using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClientHub.Application;
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
    public void EveryApiOperationHasExplicitAuthorizationMetadataAndIsInTheGeneratedInventory()
    {
        var descriptions = GetApiDescriptions(factory)
            .Where(description => description.HttpMethod is not null &&
                description.RelativePath?.StartsWith("api/", StringComparison.OrdinalIgnoreCase) == true)
            .Select(description => $"{description.HttpMethod!.ToUpperInvariant()} {description.RelativePath}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(route => route, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var routes = GetRoutes(factory).ToArray();

        Assert.Equal(descriptions, routes.Select(route => route.Operation)
            .OrderBy(route => route, StringComparer.OrdinalIgnoreCase));
        Assert.All(routes, route => Assert.True(route.IsProtected || route.IsAnonymous,
            $"{route.Operation} has neither authorization nor explicit anonymous metadata."));
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
    public async Task ClientACannotJoinClientBProjectSignalRGroup()
    {
        Guid projectBId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClientHubDbContext>();
            var clientB = await db.Clients.IgnoreQueryFilters()
                .SingleAsync(client => client.CompanyName == "Juniper & Co.");
            projectBId = await db.Projects.IgnoreQueryFilters()
                .Where(project => project.ClientId == clientB.Id)
                .Select(project => project.Id).SingleAsync();
        }

        using var client = factory.CreateClient();
        var token = await Login(client, "clienta@demo.com");
        using var negotiateRequest = new HttpRequestMessage(HttpMethod.Post,
            $"/hubs/projects/{projectBId}/negotiate?negotiateVersion=1");
        negotiateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var negotiateResponse = await client.SendAsync(negotiateRequest);
        negotiateResponse.EnsureSuccessStatusCode();
        using var negotiateBody = JsonDocument.Parse(await negotiateResponse.Content.ReadAsStringAsync());
        var connectionToken = negotiateBody.RootElement.GetProperty("connectionToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(connectionToken));

        var socketClient = factory.Server.CreateWebSocketClient();
        socketClient.ConfigureRequest = request =>
            request.Headers["Authorization"] = $"Bearer {token}";
        using var socket = await socketClient.ConnectAsync(
            new Uri($"ws://localhost/hubs/projects/{projectBId}?id={Uri.EscapeDataString(connectionToken!)}"),
            CancellationToken.None);
        await socket.SendAsync(
            Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"),
            WebSocketMessageType.Text, true, CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[4096];
        var closed = false;
        while (!closed)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            closed = result.MessageType == WebSocketMessageType.Close;
        }
    }

    [Fact]
    public async Task ClientAReceivesNoClientBDataAcrossTenantScopedRoutes()
    {
        Guid clientBId;
        Guid projectBId;
        Guid clientAId;
        Guid fileBId;
        Guid invoiceBId;
        Guid requestBId;
        Guid requestAttachmentBId;
        Guid userBId;
        Guid commentBId;
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
            clientAId = await db.Users.IgnoreQueryFilters()
                .Where(user => user.Email == "clienta@demo.com")
                .Select(user => user.ClientId!.Value).SingleAsync();
            var requestB = await db.ProjectRequests.IgnoreQueryFilters()
                .SingleAsync(request => request.ClientId == clientB.Id);
            clientBId = clientB.Id;
            projectBId = projectB.Id;
            userBId = userB.Id;
            fileBId = await db.ProjectFiles.IgnoreQueryFilters()
                .Where(file => file.ProjectId == projectB.Id).Select(file => file.Id).SingleAsync();
            invoiceBId = await db.Invoices.IgnoreQueryFilters()
                .Where(invoice => invoice.ClientId == clientB.Id).Select(invoice => invoice.Id).SingleAsync();
            requestBId = requestB.Id;

            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            await using var content = new MemoryStream(Encoding.UTF8.GetBytes("private client B attachment"));
            var storedPath = await storage.SaveAsync(content, CancellationToken.None);
            var requestAttachment = new ProjectRequestAttachment
            {
                ProjectRequestId = requestB.Id, ClientId = clientB.Id,
                FileName = "client-b-private.txt", StoredPath = storedPath,
                ContentType = "text/plain", Size = 27
            };
            var comment = new Comment
            {
                ProjectId = projectB.Id, ClientId = clientB.Id, AuthorId = userB.Id,
                Body = "Private comment for Client B."
            };

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
            db.AddRange(requestAttachment, comment, pendingMilestone, notification);
            await db.SaveChangesAsync();
            requestAttachmentBId = requestAttachment.Id;
            commentBId = comment.Id;
            milestoneBId = pendingMilestone.Id;
            notificationBId = notification.Id;
        }

        using var client = factory.CreateClient();
        var token = await Login(client, "clienta@demo.com");
        using (var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/requests"))
        {
            createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            createRequest.Content = JsonContent.Create(new
            {
                title = "Client A scoped request",
                description = "Client-supplied client ids must not change ownership.",
                priority = "Normal",
                clientId = clientBId
            });
            using var response = await client.SendAsync(createRequest);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var requestId = body.RootElement.GetProperty("id").GetGuid();
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ClientHubDbContext>();
            var created = await db.ProjectRequests.IgnoreQueryFilters().SingleAsync(item => item.Id == requestId);
            Assert.Equal(clientAId, created.ClientId);
        }

        var isolatedRequests = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, $"/api/projects/{projectBId}", null),
            (HttpMethod.Get, $"/api/projects/{projectBId}/activity", null),
            (HttpMethod.Get, $"/api/projects/{projectBId}/comments", null),
            (HttpMethod.Post, $"/api/projects/{projectBId}/comments", new { body = "Unauthorized cross-client comment." }),
            (HttpMethod.Put, $"/api/projects/{projectBId}/comments/{commentBId}", new { body = "Unauthorized cross-client edit." }),
            (HttpMethod.Delete, $"/api/projects/{projectBId}/comments/{commentBId}", null),
            (HttpMethod.Get, $"/api/projects/{projectBId}/files", null),
            (HttpMethod.Get, $"/api/projects/{projectBId}/files.zip", null),
            (HttpMethod.Get, $"/api/projects/{projectBId}/files/{fileBId}", null),
            (HttpMethod.Get, $"/api/projects/{projectBId}/files/{fileBId}/preview", null),
            (HttpMethod.Patch, $"/api/projects/{projectBId}/files/{fileBId}", new { fileName = "renamed.pdf" }),
            (HttpMethod.Delete, $"/api/projects/{projectBId}/files/{fileBId}", null),
            (HttpMethod.Get, $"/api/invoices/{invoiceBId}", null),
            (HttpMethod.Get, $"/api/invoices/{invoiceBId}/pdf", null),
            (HttpMethod.Get, $"/api/invoices/{invoiceBId}/receipt.pdf", null),
            (HttpMethod.Post, $"/api/invoices/{invoiceBId}/payment-intent", null),
            (HttpMethod.Get, $"/api/requests/{requestBId}", null),
            (HttpMethod.Delete, $"/api/requests/{requestBId}", null),
            (HttpMethod.Get, $"/api/requests/{requestBId}/attachments/{requestAttachmentBId}", null),
            (HttpMethod.Post, $"/api/projects/{projectBId}/milestones/{milestoneBId}/approval-decision", new { decision = "Approved", note = (string?)null }),
            (HttpMethod.Patch, $"/api/notifications/{notificationBId}/read", null),
            (HttpMethod.Patch, $"/api/me/team/{userBId}/deactivate", null)
        };

        foreach (var (method, path, body) in isolatedRequests)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var uploadRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{requestBId}/attachments"))
        using (var uploadBody = new MultipartFormDataContent())
        {
            uploadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes("private client B upload"));
            file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            uploadBody.Add(file, "file", "private.txt");
            uploadRequest.Content = uploadBody;
            using var response = await client.SendAsync(uploadRequest);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var uploadRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectBId}/files"))
        using (var uploadBody = new MultipartFormDataContent())
        {
            uploadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes("private client B project file"));
            file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            uploadBody.Add(file, "file", "private.txt");
            uploadRequest.Content = uploadBody;
            using var response = await client.SendAsync(uploadRequest);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        foreach (var path in new[] { "/api/projects", "/api/approvals", "/api/requests", "/api/invoices", "/api/notifications", "/api/me/overview" })
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

        using (var readAll = new HttpRequestMessage(HttpMethod.Post, "/api/notifications/read-all"))
        {
            readAll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(readAll);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClientHubDbContext>();
            var notification = await db.Notifications.IgnoreQueryFilters()
                .SingleAsync(item => item.Id == notificationBId);
            Assert.False(notification.IsRead);
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
        var routes = GetApiDescriptions(application)
            .Where(description => description.HttpMethod is not null &&
                description.RelativePath?.StartsWith("api/", StringComparison.OrdinalIgnoreCase) == true)
            .Select(description =>
            {
                var metadata = description.ActionDescriptor.EndpointMetadata ?? [];
                var allowsAnonymous = metadata.OfType<IAllowAnonymous>().Any();
                var authorization = metadata.OfType<IAuthorizeData>().ToArray();
                var path = "/" + Regex.Replace(description.RelativePath!, @"\{[^}]+\}", Guid.Empty.ToString("D"));
                return new ApiRoute(
                    $"{description.HttpMethod!.ToUpperInvariant()} {description.RelativePath}",
                    new HttpMethod(description.HttpMethod!),
                    path,
                    !allowsAnonymous && authorization.Length > 0,
                    allowsAnonymous,
                    !allowsAnonymous && authorization.Any(attribute =>
                        attribute.Roles?.Split(',').Any(role => role.Trim() == "Admin") == true));
            })
            .DistinctBy(route => route.Operation, StringComparer.OrdinalIgnoreCase);
        return routes;
    }

    private static IEnumerable<Microsoft.AspNetCore.Mvc.ApiExplorer.ApiDescription> GetApiDescriptions(
        WebApplicationFactory<Program> application) =>
        application.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.SelectMany(group => group.Items);

    private sealed record ApiRoute(
        string Operation, HttpMethod Method, string Path, bool IsProtected, bool IsAnonymous, bool IsAdminOnly);

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
