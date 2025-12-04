using System.ComponentModel;

namespace Morgana.Tools;
public class BillingTool
{
    [Description("Recupera le fatture dell'utente per un periodo specificato")]
    public async Task<string> GetInvoices(
        [Description("ID dell'utente")] string userId,
        [Description("Numero di fatture recenti da recuperare (default: 3)")] int count = 3)
    {
        // Simulazione recupero da storage/database
        await Task.Delay(100);

        string[] invoices = new[]
        {
            "Fattura Nov 2024: €150.00 - Scadenza: 15/12/2024",
            "Fattura Ott 2024: €150.00 - Pagata il: 14/11/2024",
            "Fattura Set 2024: €150.00 - Pagata il: 13/10/2024"
        };

        return string.Join("\n", invoices.Take(count));
    }

    [Description("Recupera i dettagli di una specifica fattura")]
    public async Task<string> GetInvoiceDetails(
        [Description("ID della fattura")] string invoiceId)
    {
        await Task.Delay(100);
        return $"Fattura {invoiceId}:\n- Importo: €150.00\n- Periodo: Nov 2024\n- Scadenza: 15/12/2024\n- Stato: Da pagare";
    }
}
