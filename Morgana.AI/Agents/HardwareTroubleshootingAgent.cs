using Akka.Actor;
using Microsoft.Agents.AI;
using Morgana.AI.Adapters;
using Morgana.AI.Actors;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Agents;

public class HardwareTroubleshootingAgent : MorganaActor
{
    private readonly AIAgent aiAgent;
    private readonly ILogger<HardwareTroubleshootingAgent> logger;

    public HardwareTroubleshootingAgent(
        string conversationId,
        ILLMService llmService,
        ILogger<HardwareTroubleshootingAgent> logger) : base(conversationId)
    {
        this.logger = logger;

        AgentAdapter adapter = new AgentAdapter(llmService.GetChatClient());
        aiAgent = adapter.CreateTroubleshootingAgent();

        ReceiveAsync<Records.AgentRequest>(ExecuteTroubleshootingAsync);
    }

    private async Task ExecuteTroubleshootingAsync(Records.AgentRequest req)
    {
        IActorRef senderRef = Sender;

        try
        {
            logger.LogInformation($"Executing troubleshooting for conversation {req.ConversationId}");

            AgentRunResponse response = await aiAgent.RunAsync(req.Content);

            Sender.Tell(new Records.AgentResponse(response.Text ?? "Diagnostica completata."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing troubleshooting agent");
            Sender.Tell(new Records.AgentResponse("Errore durante la diagnostica. Contatti il supporto tecnico."));
        }
    }
}