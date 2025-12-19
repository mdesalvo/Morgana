using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;

namespace Morgana.AI.Tools;

public class BillingTool : MorganaTool
{
    public BillingTool(ILogger<MorganaAgent> logger, Dictionary<string, object> context) : base(logger, context) { }

    private readonly string[] invoices =
    [
        "A555 - Periodo: Ott 2025 / Nov 2025 - Importo: €130 - Stato: Da pagare (entro il 15/12/2025)",
        "B222 - Periodo: Set 2025 / Ott 2025 - Importo: €150 - Stato: Pagata (in data 14/11/2025)",
        "C333 - Periodo: Giu 2025 / Set 2025 - Importo: €125 - Stato: Pagata (in data 13/10/2025)",
        "Z999 - Periodo: Mag 2025 / Giu 2025 - Importo: €100 - Stato: Pagata (in data 13/09/2025)"
    ];

    public async Task<string> GetInvoices(string userId, int count)
    {
        // Simulazione recupero da storage/database
        await Task.Delay(50);

        return string.Join("\n", invoices.Take(count));
    }

    public async Task<string> GetInvoiceDetails(string invoiceId)
    {
        // Simulazione recupero da storage/database
        await Task.Delay(50);

        return invoices.FirstOrDefault(inv => inv.StartsWith(invoiceId, StringComparison.OrdinalIgnoreCase))
                ?? $"Non ho trovato alcuna fattura con identificativo {invoiceId}";
    }
}
