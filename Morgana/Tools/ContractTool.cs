using System.ComponentModel;

namespace Morgana.Tools;

public class ContractTool
{
    [Description("Recupera i dettagli del contratto attivo dell'utente")]
    public async Task<string> GetContractDetails([Description("ID dell'utente")] string userId)
    {
        await Task.Delay(100);
        return "Contratto attivo:\n- Piano: Premium 100Mbps\n- Inizio: 01/01/2024\n- Scadenza: 31/12/2024\n- Canone mensile: €150.00";
    }

    [Description("Inizia procedura di disdetta contratto")]
    public async Task<string> InitiateCancellation(
        [Description("ID dell'utente")] string userId,
        [Description("Motivo della disdetta")] string reason)
    {
        await Task.Delay(200);
        return $"Procedura di disdetta avviata per utente {userId}.\nMotivo registrato: {reason}\n\nProssimi passi:\n1. Invio email conferma entro 24h\n2. Preavviso 30 giorni\n3. Disattivazione servizio";
    }
}