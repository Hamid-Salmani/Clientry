using ClientHub.Domain;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ClientHub.Api.Services;

public sealed class InvoicePdfService
{
    public byte[] Generate(Invoice invoice, string companyName)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(document => document.Page(page =>
        {
            page.Margin(48);
            page.DefaultTextStyle(style => style.FontSize(11).FontColor("#25324B"));
            page.Header().Row(row =>
            {
                row.RelativeItem().Column(column =>
                {
                    column.Item().Text("CLIENTHUB").FontSize(12).Bold().FontColor("#5B6CE1");
                    column.Item().PaddingTop(14).Text("INVOICE").FontSize(30).Bold();
                    column.Item().Text(invoice.Number).FontColor("#64748B");
                });
                row.ConstantItem(170).AlignRight().Column(column =>
                {
                    column.Item().Text(companyName).Bold();
                    column.Item().PaddingTop(8).Text($"Issued {invoice.IssueDate:MMM dd, yyyy}");
                    column.Item().Text($"Due {invoice.DueDate:MMM dd, yyyy}");
                });
            });
            page.Content().PaddingVertical(32).Column(column =>
            {
                column.Item().Text("BILL TO").FontSize(9).Bold().FontColor("#64748B");
                column.Item().PaddingTop(5).Text(companyName).FontSize(15).Bold();
                column.Item().PaddingTop(30).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(4);
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                    });
                    table.Header(header =>
                    {
                        header.Cell().Element(HeaderCell).Text("Description");
                        header.Cell().Element(HeaderCell).AlignRight().Text("Qty");
                        header.Cell().Element(HeaderCell).AlignRight().Text("Rate");
                        header.Cell().Element(HeaderCell).AlignRight().Text("Amount");
                    });
                    foreach (var line in invoice.Lines)
                    {
                        table.Cell().Element(BodyCell).Text(line.Description);
                        table.Cell().Element(BodyCell).AlignRight().Text(line.Quantity.ToString("0.##"));
                        table.Cell().Element(BodyCell).AlignRight().Text(line.UnitPrice.ToString("C"));
                        table.Cell().Element(BodyCell).AlignRight().Text((line.Quantity * line.UnitPrice).ToString("C"));
                    }
                });
                column.Item().AlignRight().PaddingTop(20).Text($"Total  {invoice.Total.ToString("C")}").FontSize(18).Bold();
            });
            page.Footer().AlignCenter().Text($"Status: {invoice.Status}  ·  Thank you for your business").FontColor("#64748B");
        })).GeneratePdf();
    }

    private static IContainer HeaderCell(IContainer container) =>
        container.BorderBottom(1).BorderColor("#E2E8F0").PaddingVertical(8).DefaultTextStyle(style => style.Bold());

    private static IContainer BodyCell(IContainer container) =>
        container.BorderBottom(1).BorderColor("#E2E8F0").PaddingVertical(12);
}
