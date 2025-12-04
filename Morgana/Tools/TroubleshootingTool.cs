using System.ComponentModel;

namespace Morgana.Tools;

public class TroubleshootingTool
{
    [Description("Esegue diagnostica sulla connessione dell'utente")]
    public async Task<string> RunDiagnostics([Description("ID dell'utente")] string userId)
    {
        await Task.Delay(300);
        return "Diagnostica completata:\n✓ Modem: Online\n✓ Connessione: Stabile\n✓ Velocità: 98Mbps (target: 100Mbps)\n⚠ Latenza leggermente alta: 25ms";
    }

    [Description("Fornisce una guida step-by-step per risolvere un problema comune")]
    public async Task<string> GetTroubleshootingGuide(
        [Description("Tipo di problema (es: 'no-internet', 'slow-connection', 'wifi-issues')")] string issueType)
    {
        await Task.Delay(100);

        var guides = new Dictionary<string, string>
        {
            ["no-internet"] = "Soluzione 'Nessuna connessione':\n1. Verifichi che il modem sia acceso\n2. Controlli i cavi di connessione\n3. Riavvii il modem (spegnere 30 sec)\n4. Se persiste, contatti assistenza",
            ["slow-connection"] = "Soluzione 'Connessione lenta':\n1. Chiuda applicazioni non necessarie\n2. Verifichi dispositivi connessi al wifi\n3. Avvicini il dispositivo al router\n4. Riavvii il router",
            ["wifi-issues"] = "Soluzione 'Problemi WiFi':\n1. Verifichi password WiFi corretta\n2. Dimentichi la rete e riconnetta\n3. Cambi canale WiFi nel router\n4. Aggiorni driver scheda di rete"
        };

        return guides.TryGetValue(issueType, out string? guide)
            ? guide
            : "Guida non trovata. Tipi disponibili: no-internet, slow-connection, wifi-issues";
    }
}