using ClientHub.Domain;
using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace ClientHub.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/payments/stripe-webhook")]
public sealed class StripeWebhookController(
    ClientHubDbContext db,
    IConfiguration configuration,
    ILogger<StripeWebhookController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        var secret = configuration["Stripe:WebhookSecret"];
        if (string.IsNullOrWhiteSpace(secret)) return Problem(statusCode: 503, title: "Payment webhook is not configured.");
        var signature = Request.Headers["Stripe-Signature"].ToString();
        string payload;
        using (var reader = new StreamReader(Request.Body))
            payload = await reader.ReadToEndAsync(cancellationToken);
        Event stripeEvent;
        try { stripeEvent = EventUtility.ConstructEvent(payload, signature, secret); }
        catch (StripeException exception)
        {
            logger.LogWarning("Rejected Stripe webhook with invalid signature: {Message}", exception.Message);
            return BadRequest();
        }

        if (stripeEvent.Type != "payment_intent.succeeded") return Ok();
        var intent = stripeEvent.Data.Object as PaymentIntent;
        if (intent is null || !intent.Metadata.TryGetValue("invoiceId", out var invoiceText) ||
            !Guid.TryParse(invoiceText, out var invoiceId)) return BadRequest();
        if (await db.Payments.AnyAsync(x => x.StripePaymentIntentId == intent.Id, cancellationToken)) return Ok();
        var invoice = await db.Invoices.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == invoiceId, cancellationToken);
        if (invoice is null) return NotFound();
        var expectedAmount = (long)decimal.Round(invoice.Total * 100, 0, MidpointRounding.AwayFromZero);
        if (intent.AmountReceived != expectedAmount ||
            !string.Equals(intent.Currency, invoice.Currency, StringComparison.OrdinalIgnoreCase) ||
            invoice.Status is not (InvoiceStatus.Sent or InvoiceStatus.Overdue))
            return BadRequest();
        db.Payments.Add(new Payment
        {
            InvoiceId = invoice.Id, StripePaymentIntentId = intent.Id,
            Amount = invoice.Total, Status = "Succeeded", PaidAt = DateTime.UtcNow
        });
        invoice.Status = InvoiceStatus.Paid;
        await db.SaveChangesAsync(cancellationToken);
        return Ok();
    }
}
