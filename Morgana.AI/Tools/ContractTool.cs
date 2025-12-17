namespace Morgana.AI.Tools;

public class ContractTool
{
    public async Task<string> GetContractDetails(string userId)
    {
        await Task.Delay(100);
        return "Contratto attivo:\n- Piano: Premium 100Mbps\n- Limitazione di traffico: 250GB/mese\n- Inizio: 01/01/2025\n- Scadenza: 31/12/2025\n- Canone mensile: €150.00";
    }

    public async Task<string> InitiateCancellation(string userId, string reason)
    {
        await Task.Delay(200);
        return $"Procedura di disdetta avviata per utente {userId}.\nMotivo registrato: {reason}\n\nProssimi passi:\n1. Invio email conferma entro 24h\n2. Preavviso 30 giorni\n3. Disattivazione servizio";
    }
}