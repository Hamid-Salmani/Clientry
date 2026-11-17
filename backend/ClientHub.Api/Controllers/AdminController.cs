using ClientHub.Domain;
using ClientHub.Application;
using ClientHub.Api.Services;
using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace ClientHub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin")]
public sealed class AdminController(ClientHubDbContext db, IEmailSender emailSender, IConfiguration configuration) : ControllerBase
{
    [HttpGet("workspace-settings")]
    public async Task<IActionResult> GetWorkspaceSettings(CancellationToken cancellationToken)
    {
        var settings = await db.WorkspaceSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        return Ok(settings ?? new WorkspaceSettings { Name = "Clientry", SupportEmail = "support@clientry.example" });
    }

    [HttpPut("workspace-settings")]
    public async Task<IActionResult> UpdateWorkspaceSettings(WorkspaceSettingsRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
            return BadRequest(new { message = "A workspace name of at most 120 characters is required." });
        if (!TryNormalizeEmail(request.SupportEmail, out var supportEmail))
            return BadRequest(new { message = "A valid support email is required." });

        var settings = await db.WorkspaceSettings.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (settings is null)
        {
            settings = new WorkspaceSettings { Id = 1, Name = request.Name.Trim(), SupportEmail = supportEmail };
            db.WorkspaceSettings.Add(settings);
        }
        else
        {
            settings.Name = request.Name.Trim();
            settings.SupportEmail = supportEmail;
            settings.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("clients/{clientId:guid}/invitations")]
    public async Task<IActionResult> InviteClientUser(Guid clientId, InvitationRequest request, CancellationToken cancellationToken)
    {
        var client = await db.Clients.SingleOrDefaultAsync(x => x.Id == clientId, cancellationToken);
        if (client is null) return NotFound();
        if (!client.IsActive) return Conflict(new { message = "Invitations cannot be created for an inactive client." });
        if (!TryNormalizeEmail(request.Email, out var email) ||
            string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length > 120)
            return BadRequest(new { message = "A valid email and name of at most 120 characters are required." });
        if (await db.Users.AnyAsync(x => x.Email.ToLower() == email, cancellationToken))
            return Conflict(new { message = "An account with this email already exists." });

        var now = DateTime.UtcNow;
        var pending = await db.Invitations.SingleOrDefaultAsync(x =>
            x.ClientId == clientId && x.Email.ToLower() == email && !x.AcceptedAt.HasValue &&
            !x.RevokedAt.HasValue && x.ExpiresAt > now, cancellationToken);
        if (pending is not null)
            return Conflict(new { message = "A pending invitation already exists for this email." });

        var token = TokenSecurity.CreateToken();
        var invitation = new Invitation
        {
            Email = email,
            FullName = request.FullName.Trim(),
            TokenHash = TokenSecurity.HashToken(token),
            ClientId = clientId,
            ExpiresAt = now.AddDays(7)
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken);
        await SendInvitationAsync(invitation, client.CompanyName, token, cancellationToken);
        return CreatedAtAction(nameof(ListInvitations), new { clientId }, new { invitation.Id, invitation.Email, invitation.FullName, invitation.ExpiresAt });
    }

    [HttpGet("clients/{clientId:guid}/invitations")]
    public async Task<IActionResult> ListInvitations(Guid clientId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (!await db.Clients.AnyAsync(x => x.Id == clientId, cancellationToken)) return NotFound();
        var now = DateTime.UtcNow;
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Invitations.AsNoTracking()
            .Where(x => x.ClientId == clientId && !x.AcceptedAt.HasValue && !x.RevokedAt.HasValue && x.ExpiresAt > now);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(invitation => invitation.Email.ToLower().Contains(term) ||
                invitation.FullName.ToLower().Contains(term));
        }
        var total = await query.CountAsync(cancellationToken);
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);
        var invitations = await query.OrderBy(x => x.Email).Skip(skip).Take(pageSize)
            .Select(x => new { x.Id, x.Email, x.FullName, x.ExpiresAt }).ToListAsync(cancellationToken);
        return Ok(new PagedResult<object>(invitations.Cast<object>().ToArray(), total, page, pageSize));
    }

    [HttpPost("clients/{clientId:guid}/invitations/{invitationId:guid}/resend")]
    public async Task<IActionResult> ResendInvitation(Guid clientId, Guid invitationId, CancellationToken cancellationToken)
    {
        var invitation = await db.Invitations.SingleOrDefaultAsync(x =>
            x.Id == invitationId && x.ClientId == clientId, cancellationToken);
        if (invitation is null) return NotFound();
        if (invitation.AcceptedAt.HasValue || invitation.RevokedAt.HasValue)
            return Conflict(new { message = "Only pending invitations can be resent." });

        var client = await db.Clients.SingleOrDefaultAsync(x => x.Id == clientId, cancellationToken);
        if (client is null) return NotFound();
        if (!client.IsActive) return Conflict(new { message = "Invitations cannot be resent for an inactive client." });
        var token = TokenSecurity.CreateToken();
        invitation.TokenHash = TokenSecurity.HashToken(token);
        invitation.ExpiresAt = DateTime.UtcNow.AddDays(7);
        await db.SaveChangesAsync(cancellationToken);
        await SendInvitationAsync(invitation, client.CompanyName, token, cancellationToken);
        return NoContent();
    }

    [HttpDelete("clients/{clientId:guid}/invitations/{invitationId:guid}")]
    public async Task<IActionResult> RevokeInvitation(Guid clientId, Guid invitationId, CancellationToken cancellationToken)
    {
        var invitation = await db.Invitations.SingleOrDefaultAsync(x =>
            x.Id == invitationId && x.ClientId == clientId, cancellationToken);
        if (invitation is null) return NotFound();
        if (invitation.AcceptedAt.HasValue || invitation.RevokedAt.HasValue)
            return Conflict(new { message = "Only pending invitations can be revoked." });
        invitation.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("clients")]
    public async Task<IActionResult> Clients(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? sortBy = "companyName",
        [FromQuery] bool descending = false,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Clients.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(client =>
                client.CompanyName.ToLower().Contains(term) ||
                client.Notes.ToLower().Contains(term) ||
                client.Users.Any(user => user.FullName.ToLower().Contains(term) || user.Email.ToLower().Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var ordered = (sortBy?.ToLowerInvariant(), descending) switch
        {
            ("projectcount", false) => query.OrderBy(client => client.Projects.Count(project => project.Status != ProjectStatus.Archived)),
            ("projectcount", true) => query.OrderByDescending(client => client.Projects.Count(project => project.Status != ProjectStatus.Archived)),
            ("status", false) => query.OrderBy(client => client.IsActive),
            ("status", true) => query.OrderByDescending(client => client.IsActive),
            (_, true) => query.OrderByDescending(client => client.CompanyName),
            _ => query.OrderBy(client => client.CompanyName)
        };
        var clients = await ordered.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(client => new
            {
                client.Id,
                client.CompanyName,
                contact = client.Users.Where(user => user.IsActive).OrderBy(user => user.FullName)
                    .Select(user => user.FullName).FirstOrDefault(),
                activeProjects = client.Projects.Count(project => project.Status != ProjectStatus.Archived),
                client.IsActive
            }).ToListAsync(cancellationToken);
        return Ok(new PagedResult<object>(clients.Cast<object>().ToArray(), total, page, pageSize));
    }

    [HttpPost("clients")]
    public async Task<IActionResult> CreateClient(ClientRequest request, CancellationToken cancellationToken)
    {
        if (!TryValidateClient(request, out var companyName, out var notes, out var error))
            return BadRequest(new { message = error });
        var client = new Client { CompanyName = companyName, Notes = notes };
        db.Clients.Add(client);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(ClientDetails), new { clientId = client.Id }, new { client.Id });
    }

    [HttpGet("clients/{clientId:guid}")]
    public async Task<IActionResult> ClientDetails(Guid clientId, CancellationToken cancellationToken)
    {
        var client = await db.Clients.AsNoTracking().Include(x => x.Users)
            .SingleOrDefaultAsync(x => x.Id == clientId, cancellationToken);
        if (client is null) return NotFound();
        var outstandingBalance = await db.InvoiceLineItems
            .Where(line => line.Invoice!.ClientId == clientId &&
                (line.Invoice.Status == InvoiceStatus.Sent || line.Invoice.Status == InvoiceStatus.Overdue))
            .SumAsync(line => (decimal?)(line.Quantity * line.UnitPrice), cancellationToken) ?? 0m;
        var lifetimeRevenue = await db.Payments
            .Where(payment => payment.Invoice!.ClientId == clientId && payment.Status == "Succeeded")
            .SumAsync(payment => (decimal?)payment.Amount, cancellationToken) ?? 0m;
        return Ok(new
        {
            client.Id,
            client.CompanyName,
            client.Notes,
            client.IsActive,
            projectCount = await db.Projects.CountAsync(project => project.ClientId == clientId, cancellationToken),
            outstandingBalance,
            lifetimeRevenue,
            users = client.Users.OrderBy(user => user.FullName).Select(user => new
            {
                user.Id, user.FullName, user.Email, user.IsActive, clientRole = user.ClientRole.ToString()
            })
        });
    }

    [HttpPut("clients/{clientId:guid}/owner")]
    public async Task<IActionResult> SetClientOwner(Guid clientId, ClientOwnerRequest request, CancellationToken cancellationToken)
    {
        if (!await db.Clients.AnyAsync(client => client.Id == clientId, cancellationToken)) return NotFound();
        var users = await db.Users.Where(user => user.ClientId == clientId && user.Role == UserRole.Client)
            .ToListAsync(cancellationToken);
        var owner = users.SingleOrDefault(user => user.Id == request.UserId && user.IsActive);
        if (owner is null) return NotFound();

        foreach (var user in users)
            user.ClientRole = user.Id == owner.Id ? ClientRole.Owner : ClientRole.Member;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPut("clients/{clientId:guid}")]
    public async Task<IActionResult> UpdateClient(Guid clientId, ClientRequest request, CancellationToken cancellationToken)
    {
        var client = await db.Clients.SingleOrDefaultAsync(x => x.Id == clientId, cancellationToken);
        if (client is null) return NotFound();
        if (!TryValidateClient(request, out var companyName, out var notes, out var error))
            return BadRequest(new { message = error });
        client.CompanyName = companyName;
        client.Notes = notes;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPatch("clients/{clientId:guid}/status")]
    public async Task<IActionResult> SetClientStatus(Guid clientId, SetActiveRequest request, CancellationToken cancellationToken)
    {
        var client = await db.Clients.SingleOrDefaultAsync(x => x.Id == clientId, cancellationToken);
        if (client is null) return NotFound();
        client.IsActive = request.IsActive;
        if (!client.IsActive)
        {
            var userIds = db.Users.Where(user => user.ClientId == clientId).Select(user => user.Id);
            var refreshTokens = await db.RefreshTokens
                .Where(token => userIds.Contains(token.UserId) && !token.RevokedAt.HasValue)
                .ToListAsync(cancellationToken);
            var now = DateTime.UtcNow;
            foreach (var refreshToken in refreshTokens) refreshToken.RevokedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPatch("clients/{clientId:guid}/users/{userId:guid}/status")]
    public async Task<IActionResult> SetClientUserStatus(Guid clientId, Guid userId, SetActiveRequest request, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(x =>
            x.Id == userId && x.ClientId == clientId && x.Role == UserRole.Client, cancellationToken);
        if (user is null) return NotFound();
        if (!request.IsActive && user.ClientRole == ClientRole.Owner)
            return Conflict(new { message = "Assign another owner before deactivating the current owner." });
        user.IsActive = request.IsActive;
        if (!user.IsActive)
        {
            var now = DateTime.UtcNow;
            var refreshTokens = await db.RefreshTokens.Where(x => x.UserId == userId && !x.RevokedAt.HasValue)
                .ToListAsync(cancellationToken);
            foreach (var refreshToken in refreshTokens) refreshToken.RevokedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var currentMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var firstMonth = currentMonth.AddMonths(-5);
        var recentPayments = await db.Payments.AsNoTracking()
            .Where(payment => payment.PaidAt.HasValue && payment.PaidAt.Value >= firstMonth)
            .Select(payment => new { payment.PaidAt, payment.Amount }).ToListAsync(cancellationToken);
        var revenue = recentPayments.Where(payment => payment.PaidAt.HasValue)
            .GroupBy(payment => (payment.PaidAt!.Value.Year, payment.PaidAt.Value.Month))
            .ToDictionary(group => group.Key, group => group.Sum(payment => payment.Amount));
        var revenueByMonth = Enumerable.Range(0, 6).Select(offset =>
        {
            var month = firstMonth.AddMonths(offset);
            return new
            {
                month = month.ToString("MMM"),
                revenue = revenue.TryGetValue((month.Year, month.Month), out var amount) ? amount : 0m
            };
        }).ToArray();
        var projectStatusGroups = await db.Projects.AsNoTracking().GroupBy(project => project.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() }).ToListAsync(cancellationToken);
        return Ok(new
        {
            activeProjects = await db.Projects.CountAsync(project =>
                project.Status == ProjectStatus.InProgress || project.Status == ProjectStatus.InReview, cancellationToken),
            clientCount = await db.Clients.CountAsync(cancellationToken),
            revenueThisMonth = revenue.TryGetValue((today.Year, today.Month), out var currentRevenue) ? currentRevenue : 0m,
            unpaidInvoices = await db.Invoices.CountAsync(invoice => invoice.Status == InvoiceStatus.Sent, cancellationToken),
            overdueInvoices = await db.Invoices.CountAsync(invoice => invoice.Status == InvoiceStatus.Overdue, cancellationToken),
            projectStatus = projectStatusGroups.Select(group => new { status = group.Status.ToString(), count = group.Count }),
            revenueByMonth,
            recentActivity = await db.ActivityLogs.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(6)
                .Select(x => new { x.Action, x.Metadata, x.CreatedAt }).ToListAsync(cancellationToken)
        });
    }

    private async Task SendInvitationAsync(Invitation invitation, string clientName, string token, CancellationToken cancellationToken)
    {
        var baseUrl = (configuration["Frontend:BaseUrl"] ?? "http://localhost:5173").TrimEnd('/');
        var inviteUrl = $"{baseUrl}/accept-invite/{Uri.EscapeDataString(token)}";
        await emailSender.SendAsync(invitation.Email, $"You're invited to {clientName}",
            $"<p>Hello {WebUtility.HtmlEncode(invitation.FullName)},</p><p>You have been invited to join {WebUtility.HtmlEncode(clientName)} on Clientry.</p><p><a href=\"{WebUtility.HtmlEncode(inviteUrl)}\">Accept invitation</a></p><p>This link expires in seven days.</p>",
            null, cancellationToken);
    }

    private static bool TryNormalizeEmail(string? value, out string email)
    {
        email = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return email.Length <= 254 && System.Net.Mail.MailAddress.TryCreate(email, out var address) &&
            string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryValidateClient(ClientRequest request, out string companyName, out string notes, out string error)
    {
        companyName = request.CompanyName?.Trim() ?? string.Empty;
        notes = request.Notes?.Trim() ?? string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(companyName) || companyName.Length > 160 || notes.Length > 4000)
        {
            error = "Company name is required (max 160 characters) and notes must be at most 4,000 characters.";
            return false;
        }
        return true;
    }

    public sealed record ClientRequest(string CompanyName, string Notes);
    public sealed record ClientOwnerRequest(Guid UserId);
    public sealed record SetActiveRequest(bool IsActive);
    public sealed record InvitationRequest(string Email, string FullName);
    public sealed record WorkspaceSettingsRequest(string Name, string SupportEmail);
}
