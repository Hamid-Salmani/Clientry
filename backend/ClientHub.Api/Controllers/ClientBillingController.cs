using System.Security.Claims;
using ClientHub.Domain;
using ClientHub.Infrastructure;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClientHub.Api.Controllers;

[ApiController]
[Authorize(Roles = "Client")]
[Route("api/me/billing")]
public sealed class ClientBillingController(
    ClientHubDbContext db,
    IValidator<ClientBillingController.BillingDetailsRequest> validator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        if (client is null) return NotFound();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return NotFound();
        var canEdit = await db.Users.AsNoTracking().AnyAsync(user =>
            user.Id == userId && user.ClientRole == ClientRole.Owner, cancellationToken);
        return Ok(new { client.LegalName, client.TaxId, client.BillingEmails, client.BillingAddress, canEdit });
    }

    [HttpPut]
    public async Task<IActionResult> Update(BillingDetailsRequest request, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        if (client is null) return NotFound();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return NotFound();
        var isOwner = await db.Users.AsNoTracking().AnyAsync(user =>
            user.Id == userId && user.ClientRole == ClientRole.Owner, cancellationToken);
        if (!isOwner) return Forbid();
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        client.LegalName = Clean(request.LegalName);
        client.TaxId = Clean(request.TaxId);
        client.BillingEmails = Clean(request.BillingEmails);
        client.BillingAddress = Clean(request.BillingAddress);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<Client?> GetClientAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ||
            !Guid.TryParse(User.FindFirstValue("client_id"), out var clientId))
            return null;
        if (!await db.Users.AnyAsync(user => user.Id == userId && user.ClientId == clientId, cancellationToken))
            return null;
        return await db.Clients.SingleOrDefaultAsync(client => client.Id == clientId, cancellationToken);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public sealed record BillingDetailsRequest(string? LegalName, string? TaxId, string? BillingEmails, string? BillingAddress);
}
