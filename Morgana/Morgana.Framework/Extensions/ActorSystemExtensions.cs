using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.Framework.Abstractions;

namespace Morgana.Framework.Extensions;

/// <summary>
/// Extension methods for ActorSystem to simplify actor and agent creation with conversation scoping.
/// Provides "Get or Create" semantics for conversation-scoped actors with dependency injection support.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>These extensions simplify the common pattern of "get existing actor or create new one" for conversation-scoped actors.
/// All actors in Morgana are scoped to conversations, using the pattern: actorName = "{actorSuffix}-{conversationId}"</para>
/// <para><strong>Actor Naming Convention:</strong></para>
/// <code>
/// // Supervisor for conversation "conv123"
/// /user/supervisor-conv123
///
/// // Guard for conversation "conv123"
/// /user/guard-conv123
///
/// // Router for conversation "conv123"
/// /user/router-conv123
/// </code>
/// <para><strong>Benefits:</strong></para>
/// <list type="bullet">
/// <item>Prevents duplicate actor creation for the same conversation</item>
/// <item>Integrates with Akka.NET DependencyInjection for service resolution</item>
/// <item>Simplifies actor creation with consistent naming pattern</item>
/// <item>Handles both typed actors (GetOrCreateActor) and dynamic types (GetOrCreateAgent)</item>
/// </list>
/// </remarks>
public static class ActorSystemExtensions
{
    extension(ActorSystem actorSystem)
    {
        /// <summary>
        /// Gets an existing actor or creates a new one if it doesn't exist.
        /// Uses compile-time type information for strongly-typed actor creation.
        /// </summary>
        /// <typeparam name="T">Type of MorganaActor to create (e.g., GuardActor, ClassifierActor)</typeparam>
        /// <param name="actorSuffix">Actor name suffix (e.g., "guard", "classifier", "supervisor")</param>
        /// <param name="conversationId">Unique identifier of the conversation</param>
        /// <returns>Actor reference (either existing or newly created)</returns>
        /// <remarks>
        /// <para><strong>Resolution Strategy:</strong></para>
        /// <list type="number">
        /// <item>Construct actor path: /user/{actorSuffix}-{conversationId}</item>
        /// <item>Attempt to resolve existing actor with 250ms timeout</item>
        /// <item>If found, return existing actor reference</item>
        /// <item>If not found, create new actor with DependencyResolver</item>
        /// <item>Return newly created actor reference</item>
        /// </list>
        /// <para><strong>Dependency Injection:</strong></para>
        /// <para>Actors are created using DependencyResolver, which automatically resolves constructor dependencies
        /// from the ASP.NET Core DI container (ILLMService, IPromptResolverService, etc.)</para>
        /// <para><strong>Usage Examples:</strong></para>
        /// <code>
        /// // In ConversationSupervisorActor constructor
        /// guard = await Context.System.GetOrCreateActor&lt;GuardActor&gt;("guard", conversationId);
        /// classifier = await Context.System.GetOrCreateActor&lt;ClassifierActor&gt;("classifier", conversationId);
        /// router = await Context.System.GetOrCreateActor&lt;RouterActor&gt;("router", conversationId);
        ///
        /// // Result: /user/guard-conv123, /user/classifier-conv123, /user/router-conv123
        /// </code>
        /// </remarks>
        public async Task<IActorRef> GetOrCreateActor<T>(string actorSuffix, string conversationId)
            where T : MorganaActor
        {
            string actorName = $"{actorSuffix}-{conversationId}";

            try
            {
                // Attempt to resolve existing actor
                return await actorSystem.ActorSelection($"/user/{actorName}")
                    .ResolveOne(TimeSpan.FromMilliseconds(500));
            }
            catch
            {
                // Actor doesn't exist, create new one with DI
                Props actorProps = DependencyResolver.For(actorSystem)
                    .Props<T>(conversationId);

                return actorSystem.ActorOf(actorProps, actorName);
            }
        }

        /// <summary>
        /// Gets an existing agent or creates a new one if it doesn't exist.
        /// Uses runtime type information for dynamic agent creation (supports plugin agents).
        /// </summary>
        /// <param name="agentType">Type of MorganaAgent to create (e.g., typeof(BillingAgent))</param>
        /// <param name="actorSuffix">Agent name suffix (typically the intent name, e.g., "billing")</param>
        /// <param name="conversationId">Unique identifier of the conversation</param>
        /// <returns>Actor reference (either existing or newly created)</returns>
        /// <remarks>
        /// <para><strong>Purpose:</strong></para>
        /// <para>This overload supports dynamic agent creation where the agent type is only known at runtime.
        /// This is essential for RouterActor, which discovers agents via reflection based on [HandlesIntent] attributes.</para>
        /// <para><strong>Resolution Strategy:</strong></para>
        /// <list type="number">
        /// <item>Construct agent path: /user/{actorSuffix}-{conversationId}</item>
        /// <item>Attempt to resolve existing agent with 250ms timeout</item>
        /// <item>If found, return existing agent reference</item>
        /// <item>If not found, create new agent with DependencyResolver using runtime type</item>
        /// <item>Return newly created agent reference</item>
        /// </list>
        /// <para><strong>Usage in RouterActor:</strong></para>
        /// <code>
        /// // RouterActor constructor - autodiscovery of agents
        /// foreach (string intent in agentResolverService.GetAllIntents())
        /// {
        ///     Type? agentType = agentResolverService.ResolveAgentFromIntent(intent);
        ///     if (agentType != null)
        ///     {
        ///         // Create agent for this intent
        ///         agents[intent] = await Context.System.GetOrCreateAgent(
        ///             agentType,
        ///             intent,        // actorSuffix = intent name
        ///             conversationId
        ///         );
        ///     }
        /// }
        ///
        /// // Result for billing intent:
        /// // agents["billing"] = /user/billing-conv123
        /// </code>
        /// <para><strong>Plugin Agent Support:</strong></para>
        /// <para>This method enables plugin agents (loaded from external assemblies) to be created
        /// dynamically without compile-time knowledge of their types. The RouterActor discovers
        /// them via [HandlesIntent] attributes and creates them using this method.</para>
        /// <para><strong>Dependency Injection:</strong></para>
        /// <para>Like GetOrCreateActor, this method uses DependencyResolver to inject constructor
        /// dependencies, supporting both framework services and custom plugin services.</para>
        /// </remarks>
        public async Task<IActorRef> GetOrCreateAgent(Type agentType, string actorSuffix, string conversationId)
        {
            string agentName = $"{actorSuffix}-{conversationId}";

            try
            {
                // Attempt to resolve existing agent
                return await actorSystem.ActorSelection($"/user/{agentName}")
                    .ResolveOne(TimeSpan.FromMilliseconds(500));
            }
            catch
            {
                // Agent doesn't exist, create new one with DI using runtime type
                Props agentProps = DependencyResolver.For(actorSystem)
                    .Props(agentType, conversationId);

                return actorSystem.ActorOf(agentProps, agentName);
            }
        }
    }
}