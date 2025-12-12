using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI.Tools;

namespace Morgana.Adapters;

public class AgentAdapter
{
    private readonly IChatClient chatClient;

    public AgentAdapter(IChatClient chatClient)
    {
        this.chatClient = chatClient;
    }

    public AIAgent CreateBillingAgent()
    {
        BillingTool billingTool = new BillingTool();

        return chatClient.CreateAIAgent(
            instructions: @"Sei un assistente specializzato in fatturazione e pagamenti.
                Aiuti i clienti a recuperare fatture, verificare pagamenti e risolvere problemi di billing.
                Usa sempre un tono professionale e fornisci informazioni precise.
                Quando ti serve una informazione aggiuntiva dall’utente,
                DEVI terminare la risposta con il token speciale: #INT#

                Esempio:
                'Per favore, puoi dirmi il tuo codice utente? #INT#'

                Quando invece hai completato la procedura e non richiedi nulla,
                NON inserire il token #INT#.

                Stile: professionale, chiaro, conciso.",
            name: "BillingAgent",
            tools:
            [
                AIFunctionFactory.Create(billingTool.GetInvoices),
                AIFunctionFactory.Create(billingTool.GetInvoiceDetails)
            ]
        );
    }

    public AIAgent CreateContractAgent()
    {
        ContractCancellationTool contractTool = new ContractCancellationTool();

        return chatClient.CreateAIAgent(
            instructions: @"Sei un assistente specializzato in gestione contratti.
                Gestisci richieste su dettagli contrattuali, modifiche e disdette.
                Spiega sempre chiaramente i termini contrattuali e le procedure necessarie.
                Per disdette, raccogli il motivo e illustra i prossimi passaggi.
                Quando ti serve una informazione aggiuntiva dall’utente,
                DEVI terminare la risposta con il token speciale: #INT#

                Esempio:
                'Per favore, puoi dirmi il tuo codice utente? #INT#'

                Quando invece hai completato la procedura e non richiedi nulla,
                NON inserire il token #INT#.

                Stile: professionale, chiaro, conciso.",
            name: "ContractAgent",
            tools:
            [
                AIFunctionFactory.Create(contractTool.GetContractDetails),
                AIFunctionFactory.Create(contractTool.InitiateCancellation)
            ]);
    }

    public AIAgent CreateTroubleshootingAgent()
    {
        HardwareTroubleshootingTool troubleshootingTool = new HardwareTroubleshootingTool();

        return chatClient.CreateAIAgent(
            instructions: @"Sei un tecnico di assistenza hardware e connettività.
                Aiuti i clienti a diagnosticare e risolvere problemi tecnici.
                Fornisci guide step-by-step chiare e verificabili.
                Se il problema è complesso, escalalo al supporto tecnico avanzato.
                Quando ti serve una informazione aggiuntiva dall’utente,
                DEVI terminare la risposta con il token speciale: #INT#

                Esempio:
                'Per favore, puoi dirmi il tuo codice utente? #INT#'

                Quando invece hai completato la procedura e non richiedi nulla,
                NON inserire il token #INT#.

                Stile: professionale, chiaro, conciso.",
            name: "TroubleshootingAgent",
            tools:
            [
                AIFunctionFactory.Create(troubleshootingTool.RunDiagnostics),
                AIFunctionFactory.Create(troubleshootingTool.GetTroubleshootingGuide)
            ]);
    }
}