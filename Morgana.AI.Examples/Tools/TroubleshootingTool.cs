using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;
using Morgana.AI.Providers;
using Morgana.AI.Tools;

namespace Morgana.AI.Examples.Tools;

[ProvidesToolForIntent("troubleshooting")]
public class TroubleshootingTool : MorganaTool
{
    public TroubleshootingTool(
        ILogger<MorganaAgent> logger,
        Func<MorganaContextProvider> getContextProvider) : base(logger, getContextProvider) { }

    private readonly Dictionary<string, string> guides = new Dictionary<string, string>
    {
        ["no-internet"] = "Soluzione 'Nessuna connessione':\n1. Verifichi che il modem sia acceso\n2. Controlli i cavi di connessione\n3. Riavvii il modem (spegnere 30 sec)\n4. Se persiste, contatti assistenza",
        ["slow-connection"] = "Soluzione 'Connessione lenta':\n1. Chiuda applicazioni non necessarie\n2. Verifichi dispositivi connessi al wifi\n3. Avvicini il dispositivo al router\n4. Riavvii il router",
        ["wifi-issues"] = "Soluzione 'Problemi WiFi':\n1. Verifichi password WiFi corretta\n2. Dimentichi la rete e riconnetta\n3. Cambi canale WiFi nel router\n4. Aggiorni driver scheda di rete"
    };
    
    public async Task<string> RunDiagnostics(string userId)
    {
        await Task.Delay(300);

        return "Diagnostica completata:\n✓ Modem: Online\n✓ Connessione: Stabile\n✓ Velocità: 98Mbps (target: 100Mbps)\n⚠ Latenza leggermente alta: 25ms";
    }

    public async Task<string> GetTroubleshootingGuide(string issueType)
    {
        await Task.Delay(100);

        return guides.GetValueOrDefault(issueType, "Guida non trovata. Tipi disponibili: no-internet, slow-connection, wifi-issues");
    }
}