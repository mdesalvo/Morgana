using Akka.Actor;
using Morgana.Interfaces;
using Microsoft.Agents.AI;
using static Morgana.Records;
using Morgana.Actors;
using Morgana.AI.Adapters;

namespace Morgana.AI.Agents;

public class HardwareTroubleshootingAgent : MorganaActor
{
    private readonly AIAgent aiAgent;
    private readonly ILogger<HardwareTroubleshootingAgent> logger;

    public HardwareTroubleshootingAgent(string conversationId, ILLMService llmService,
        ILogger<HardwareTroubleshootingAgent> logger) : base(conversationId)
    {
        this.logger = logger;

        AgentAdapter adapter = new AgentAdapter(llmService.GetChatClient());
        aiAgent = adapter.CreateTroubleshootingAgent();

        ReceiveAsync<ExecuteRequest>(ExecuteTroubleshootingAsync);
    }

    private async Task ExecuteTroubleshootingAsync(ExecuteRequest req)
    {
        IActorRef senderRef = Sender;

        try
        {
            logger.LogInformation($"Executing troubleshooting for conversation {req.ConversationId}");

            AgentRunResponse response = await aiAgent.RunAsync(req.Content);

            Sender.Tell(new AgentResponse(response.Text ?? "Diagnostica completata."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing troubleshooting agent");
            Sender.Tell(new AgentResponse("Errore durante la diagnostica. Contatti il supporto tecnico."));
        }
    }
}