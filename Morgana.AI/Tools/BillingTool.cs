using System.ComponentModel;

namespace Morgana.AI.Tools;

public class BillingTool
{
    [Description("Recupera le fatture dell'utente per un periodo specificato")]
    public async Task<string> GetInvoices(
        [Description("Identificativo alfanumerico dell'utente")] string userId,
        [Description("Numero di fatture recenti da recuperare (default: 3)")] int count = 3)
    {
        // Simulazione recupero da storage/database
        await Task.Delay(50);

        string[] invoices =
        [
            "Fattura A555 - Nov 2024: €150.00 - Scadenza: 15/12/2024",
            "Fattura B222 - Ott 2024: €150.00 - Pagata il: 14/11/2024",
            "Fattura C333 - Set 2024: €150.00 - Pagata il: 13/10/2024"
        ];

        return string.Join("\n", invoices.Take(count));
    }

    [Description("Recupera i dettagli di una specifica fattura")]
    public async Task<string> GetInvoiceDetails(
        [Description("ID della fattura")] string invoiceId)
    {
        // Simulazione recupero da storage/database
        await Task.Delay(50);

        return $"Fattura {invoiceId}:\n- Importo: €150.00\n- Periodo: Nov 2024\n- Scadenza: 15/12/2024\n- Stato: Da pagare";
    }
}
