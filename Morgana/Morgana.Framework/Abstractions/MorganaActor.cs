using Akka.Actor;
using Akka.Event;
using Morgana.Framework.Interfaces;

namespace Morgana.Framework.Abstractions;

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
    /// <para><strong>Usage in FSM Actors:</strong></para>
    /// <code>
    /// // Example: ConversationSupervisorActor with multiple states
    /// private void AwaitingGuardCheck()
    /// {
    ///     Receive&lt;GuardCheckContext&gt;(HandleGuardCheckResult);
    ///     RegisterCommonHandlers(); // ✅ Re-register timeout handler
    /// }
    ///
    /// private void AwaitingClassification()
    /// {
    ///     Receive&lt;ClassificationContext&gt;(HandleClassificationResult);
    ///     RegisterCommonHandlers(); // ✅ Re-register timeout handler
    /// }
    /// </code>
    /// <para><strong>Design Note:</strong></para>
    /// <para>This method is protected so derived classes can call it in their behavior methods.
    /// If additional common handlers are needed in the future (e.g., PoisonPill, system messages),
    /// they should be added here to ensure all FSM states handle them consistently.</para>
    /// <para><strong>Why Not Just Override Become():</strong></para>
    /// <para>While we could override Become() to automatically re-register handlers, that would be
    /// less explicit and harder to understand. The explicit call to RegisterCommonHandlers() in each
    /// behavior makes it clear that common handlers are being maintained across state transitions.</para>
    /// </remarks>
    protected virtual void RegisterCommonHandlers()
    {
        Receive<ReceiveTimeout>(HandleReceiveTimeout);
    }
}