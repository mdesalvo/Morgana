using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.Interfaces;
using Morgana.Tools;

namespace Morgana.Adapters;

/// <summary>
/// Adapter per integrare Microsoft.Agents.Framework con gli executor di Morgana
/// </summary>
public class AgentExecutorAdapter
{
    private readonly IChatClient _chatClient;

    public AgentExecutorAdapter(IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    public AIAgent CreateBillingAgent(IStorageService storageService)
    {
        var billingTool = new BillingTool();

        return _chatClient.CreateAIAgent(
            instructions: @"Sei un assistente specializzato in fatturazione e pagamenti.
                Aiuti i clienti a recuperare fatture, verificare pagamenti e risolvere problemi di billing.
                Usa sempre un tono professionale e fornisci informazioni precise.
                Se non hai i dati richiesti, suggerisci di contattare il supporto amministrativo.",
            name: "BillingAgent",
            tools:
            [
                AIFunctionFactory.Create(billingTool.GetInvoices),
                AIFunctionFactory.Create(billingTool.GetInvoiceDetails)
            ]);
    }

    public AIAgent CreateContractAgent(IStorageService storageService)
    {
        var contractTool = new ContractTool();

        return _chatClient.CreateAIAgent(
            instructions: @"Sei un assistente specializzato in gestione contratti.
                Gestisci richieste su dettagli contrattuali, modifiche e disdette.
                Spiega sempre chiaramente i termini contrattuali e le procedure necessarie.
                Per disdette, raccogli il motivo e illustra i prossimi passaggi.",
            name: "ContractAgent",
            tools:
            [
                AIFunctionFactory.Create(contractTool.GetContractDetails),
                AIFunctionFactory.Create(contractTool.InitiateCancellation)
            ]);
    }

    public AIAgent CreateTroubleshootingAgent()
    {
        var troubleshootingTool = new TroubleshootingTool();

        return _chatClient.CreateAIAgent(
            instructions: @"Sei un tecnico di assistenza hardware e connettività.
                Aiuti i clienti a diagnosticare e risolvere problemi tecnici.
                Fornisci guide step-by-step chiare e verificabili.
                Se il problema è complesso, escalalo al supporto tecnico avanzato.",
            name: "TroubleshootingAgent",
            tools:
            [
                AIFunctionFactory.Create(troubleshootingTool.RunDiagnostics),
                AIFunctionFactory.Create(troubleshootingTool.GetTroubleshootingGuide)
            ]);
    }
}