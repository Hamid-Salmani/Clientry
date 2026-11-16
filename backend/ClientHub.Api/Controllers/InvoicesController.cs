using ClientHub.Api.Services;
using ClientHub.Domain;
using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stripe;
using DomainInvoice = ClientHub.Domain.Invoice;
using DomainInvoiceLineItem = ClientHub.Domain.InvoiceLineItem;

namespace ClientHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/invoices")]
public sealed class InvoicesController(ClientHubDbContext db, InvoicePdfService pdf) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var invoices = await db.Invoices.AsNoTracking().Include(x => x.Lines)
            .OrderByDescending(x => x.IssueDate).ToListAsync(cancellationToken);
        return Ok(invoices.Select(x => new
        {
            x.Id, x.Number, x.IssueDate, x.DueDate, status = x.Status.ToString(),
            x.Currency, total = x.Total
        }));
    }

    [HttpGet("{invoiceId:guid}/pdf")]
    public async Task<IActionResult> DownloadPdf(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().Include(x => x.Lines)
            .Include(x => x.Client)
            .FirstOrDefaultAsync(x => x.Id == invoiceId, cancellationToken);
        if (invoice is null) return NotFound();
        var content = pdf.Generate(invoice, invoice.Client?.CompanyName ?? "Client");
        return File(content, "application/pdf", $"{invoice.Number}.pdf");
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(CreateInvoiceRequest request, CancellationToken cancellationToken)
    {
        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Count > 50 ||
            request.Lines.Any(x => string.IsNullOrWhiteSpace(x.Description) || x.Description.Length > 240 ||
                x.Quantity <= 0 || x.UnitPrice < 0) || string.IsNullOrWhiteSpace(request.Currency) ||
            request.Currency.Length != 3)
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["invoice"] = ["Add valid invoice line items and a three-letter currency code."]
            }));
        if (!await db.Clients.AnyAsync(x => x.Id == request.ClientId, cancellationToken)) return NotFound();
        if (request.ProjectId.HasValue &&
            !await db.Projects.AnyAsync(x => x.Id == request.ProjectId && x.ClientId == request.ClientId, cancellationToken))
            return NotFound();

        if (!string.Equals(request.Currency, "USD", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Only USD invoices are currently supported." });
        var invoice = new DomainInvoice
        {
            ClientId = request.ClientId,
            ProjectId = request.ProjectId,
            Number = $"INV-{DateTime.UtcNow:yyyy}-{Guid.NewGuid():N}"[..16].ToUpperInvariant(),
            IssueDate = DateOnly.FromDateTime(DateTime.UtcNow),
            DueDate = request.DueDate,
            Currency = request.Currency.ToUpperInvariant(),
            Lines = request.Lines.Select(x => new DomainInvoiceLineItem
            {
                Description = x.Description.Trim(), Quantity = x.Quantity, UnitPrice = x.UnitPrice
            }).ToList()
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(List), new { id = invoice.Id }, new { invoice.Id, invoice.Number });
    }

    [HttpPost("{invoiceId:guid}/send")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Send(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(x => x.Id == invoiceId, cancellationToken);
        if (invoice is null) return NotFound();
        if (!InvoiceRules.CanTransition(invoice.Status, InvoiceStatus.Sent))
            return Conflict(new { message = "This invoice cannot be sent in its current state." });
        invoice.Status = InvoiceStatus.Sent;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{invoiceId:guid}/payment-intent")]
    public async Task<IActionResult> CreatePaymentIntent(Guid invoiceId, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == invoiceId, cancellationToken);
        if (invoice is null) return NotFound();
        if (invoice.Status is not (InvoiceStatus.Sent or InvoiceStatus.Overdue))
            return Conflict(new { message = "This invoice is not payable." });
        var secret = configuration["Stripe:SecretKey"];
        if (string.IsNullOrWhiteSpace(secret)) return Problem(statusCode: 503, title: "Payments are not configured.");
        var service = new PaymentIntentService(new StripeClient(secret));
        var intent = await service.CreateAsync(new PaymentIntentCreateOptions
        {
            Amount = (long)decimal.Round(invoice.Total * 100, 0, MidpointRounding.AwayFromZero),
            Currency = invoice.Currency.ToLowerInvariant(),
            Metadata = new Dictionary<string, string> { ["invoiceId"] = invoice.Id.ToString() },
            AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions { Enabled = true }
        }, new RequestOptions { IdempotencyKey = $"clienthub-invoice-{invoice.Id}" }, cancellationToken);
        return Ok(new { intent.ClientSecret });
    }

    public sealed record CreateInvoiceRequest(Guid ClientId, Guid? ProjectId, DateOnly DueDate, string Currency, List<InvoiceLineRequest> Lines);
    public sealed record InvoiceLineRequest(string Description, decimal Quantity, decimal UnitPrice);
}
