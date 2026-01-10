using Akka.Actor;
using Akka.Event;
using Morgana.Foundations.Interfaces;

namespace Morgana.Actors.Abstractions;

/// <summary>
/// Base class for all Morgana actors, providing common infrastructure for conversation-scoped actors.
/// Provides conversation ID tracking, LLM service access, prompt resolution, logging, and automatic timeout handling.
/// </summary>
/// <remarks>
/// <para><strong>Inheritance Hierarchy:</strong></para>
/// <code>
/// ReceiveActor (Akka.NET)
///   └── MorganaActor (base infrastructure)
///         ├── ConversationManagerActor
///         ├── ConversationSupervisorActor
///         ├── GuardActor
///         ├── ClassifierActor
///         ├── RouterActor
///         └── MorganaAgent (agent base class)
///               └── Custom domain agents (BillingAgent, ContractAgent, etc.)
/// </code>
/// <para><strong>Key Features:</strong></para>
/// <list type="bullet">
/// <item>Conversation-scoped: Each actor instance is tied to a specific conversation ID</item>
/// <item>LLM access: Direct access to ILLMService for AI completions</item>
/// <item>Prompt resolution: Access to IPromptResolverService for loading templates</item>
/// <item>Logging: Pre-configured ILoggingAdapter for actor-specific logging</item>
/// <item>Timeout handling: Global 60-second receive timeout with virtual handler for override</item>
/// </list>
/// </remarks>
public class MorganaActor : ReceiveActor
{
    /// <summary>
    /// Unique identifier of the conversation this actor is handling.
    /// Used for logging, correlation, and actor hierarchy organization.
    /// </summary>
    protected readonly string conversationId;

    /// <summary>
    /// LLM service for AI completions (Anthropic, Azure OpenAI, etc.).
    /// Provides access to chat completion APIs with conversation history management.
    /// </summary>
    protected readonly ILLMService llmService;

    /// <summary>
    /// Service for resolving prompt templates from configuration (morgana.json, agents.json).
    /// Loads system prompts, agent prompts, and dynamic templates with variable substitution.
    /// </summary>
    protected readonly IPromptResolverService promptResolverService;

    /// <summary>
    /// Akka.NET logging adapter for this actor instance.
    /// Automatically includes actor path and type in log messages.
    /// </summary>
    protected readonly ILoggingAdapter actorLogger;

    /// <summary>
    /// Initializes a new instance of MorganaActor with core infrastructure services.
    /// Sets up conversation context, services, logging, and timeout handling.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation this actor will handle</param>
    /// <param name="llmService">LLM service for AI completions</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    protected MorganaActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService)
    {
        this.conversationId = conversationId;
        this.llmService = llmService;
        this.promptResolverService = promptResolverService;
        actorLogger = Context.GetLogger();

        // Global timeout for all MorganaActor instances
        SetReceiveTimeout(TimeSpan.FromSeconds(60));
        Receive<ReceiveTimeout>(HandleReceiveTimeout);
    }

    /// <summary>
    /// Handles receive timeout when no message is received within the configured timeout period (60 seconds).
    /// Default implementation does nothing (commented warning). Override to implement custom timeout behavior.
    /// </summary>
    /// <param name="timeout">Timeout message from Akka.NET</param>
    /// <remarks>
    /// <para><strong>Purpose:</strong></para>
    /// <para>Receive timeout can be used to implement idle timeouts, cleanup, or periodic health checks.
    /// The default implementation is a no-op to avoid log spam from actors that are legitimately idle.</para>
    /// <para><strong>Override Example:</strong></para>
    /// <code>
    /// protected override void HandleReceiveTimeout(ReceiveTimeout timeout)
    /// {
    ///     actorLogger.Info("Actor idle for 60 seconds, performing cleanup");
    ///     // Implement cleanup logic
    ///     Context.Stop(Self); // Optional: stop actor on idle timeout
    /// }
    /// </code>
    /// </remarks>
    protected virtual void HandleReceiveTimeout(ReceiveTimeout timeout)
    {
        // actorLogger.Warning($"{GetType().Name} receive timeout");
    }
}