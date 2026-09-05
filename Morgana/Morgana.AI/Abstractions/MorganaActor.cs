using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Abstractions;

/// <summary>
/// Base class for all Morgana actors, providing common infrastructure for conversation-scoped actors.
/// Provides conversation ID tracking, LLM service access, prompt resolution, logging and automatic timeout handling.
/// </summary>
/// <remarks>
/// Inheritance: ReceiveActor → MorganaActor (base) → specialized actors (Manager, Supervisor,
/// Guard, Classifier, Router) + MorganaAgent. Features: conversation-scoped, LLM access,
/// prompt resolution, logging, automatic timeout handling.
/// </remarks>
public class MorganaActor : ReceiveActor
{
    /// <summary>
    /// Unique identifier of the conversation this actor is handling.
    /// Used for logging, correlation and actor hierarchy organization.
    /// </summary>
    protected readonly string conversationId;

    /// <summary>
    /// LLM service for AI completions (Anthropic, Azure OpenAI, etc.).
    /// Provides access to chat completion APIs with conversation history management.
    /// </summary>
    protected readonly ILLMService llmService;

    /// <summary>
    /// Service for resolving prompt templates from configuration (morgana.json, agents.json).
    /// Loads system prompts, agent prompts and dynamic templates with variable substitution.
    /// </summary>
    protected readonly IPromptResolverService promptResolverService;

    /// <summary>
    /// Akka.NET logging adapter for this actor instance.
    /// Automatically includes actor path and type in log messages.
    /// </summary>
    protected readonly ILoggingAdapter actorLogger;

    /// <summary>
    /// Morgana configuration (layered by ASP.NET)
    /// </summary>
    protected readonly IConfiguration configuration;

    /// <summary>
    /// Initializes a new instance of MorganaActor with core infrastructure services.
    /// Sets up conversation context, services, logging and timeout handling.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation this actor will handle</param>
    /// <param name="llmService">LLM service for AI completions</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="configuration">Morgana configuration (layered by ASP.NET)</param>
    protected MorganaActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IConfiguration configuration)
    {
        this.conversationId = conversationId;
        this.llmService = llmService;
        this.promptResolverService = promptResolverService;
        this.configuration = configuration;
        actorLogger = Context.GetLogger();

        // Global timeout for all MorganaActor instances
        SetReceiveTimeout(TimeSpan.FromSeconds(Convert.ToInt32(this.configuration["Morgana:ActorSystem:TimeoutSeconds"])));
        Receive<ReceiveTimeout>(HandleReceiveTimeout);
    }

    /// <summary>
    /// Handles receive timeout when no message is received within the configured timeout period.
    /// Default implementation does nothing (commented warning). Override to implement custom timeout behavior.
    /// </summary>
    /// <param name="timeout">Timeout message from Akka.NET</param>
    /// <remarks>
    /// <para><strong>Purpose:</strong></para>
    /// <para>Receive timeout can be used to implement idle timeouts, cleanup, or periodic health checks.
    /// The default implementation is a no-op to avoid log spam from actors that are legitimately idle.</para>
    /// </remarks>
    protected virtual void HandleReceiveTimeout(ReceiveTimeout timeout)
    {
        // actorLogger.Warning($"{GetType().Name} receive timeout");
    }

    /// <summary>
    /// Registers common message handlers that should be present in all actor behaviors.
    /// Essential for FSM actors using Become() pattern to maintain consistent message handling across states.
    /// </summary>
    /// <remarks>
    /// <para><strong>Purpose:</strong></para>
    /// <para>When actors use Become() to change behaviors (FSM pattern), the message handlers are replaced entirely.
    /// This means handlers registered in the constructor (like ReceiveTimeout) are lost unless re-registered
    /// in each behavior. This method provides a centralized way to ensure critical handlers are always present.</para>
    /// <para><strong>Currently Registered Common Handlers:</strong></para>
    /// <list type="bullet">
    /// <item><term>ReceiveTimeout</term><description>Prevents dead letters from timeout messages in FSM states</description></item>
    /// </list>
    /// </remarks>
    protected virtual void RegisterCommonHandlers()
    {
        Receive<ReceiveTimeout>(HandleReceiveTimeout);
    }
}