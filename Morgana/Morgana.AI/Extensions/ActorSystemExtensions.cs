using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.AI.Abstractions;

namespace Morgana.AI.Extensions;

/// <summary>
/// Extension methods for "get or create" actor/agent retrieval with conversation scoping. Naming pattern:
/// /user/{actorSuffix}-{conversationId}. Prevents duplicate creation, supports DI resolution for both
/// typed actors (GetOrCreateActorAsync&lt;T&gt;) and plugin agents (GetOrCreateAgentAsync). Only supports MorganaActor subclasses.
/// </summary>
public static class ActorSystemExtensions
{
    extension(ActorSystem actorSystem)
    {
        /// <summary>
        /// Gets existing actor or creates new one with compile-time type safety. Path: /user/{actorSuffix}-{conversationId}.
        /// 250ms timeout on ActorSelection; if not found creates via DependencyResolver (injects all constructor dependencies).
        /// </summary>
        public async Task<IActorRef> GetOrCreateActorAsync<T>(string actorSuffix, string conversationId)
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
        /// Gets existing agent or creates new one with runtime type info (supports plugin discovery). Used by RouterActor
        /// to create agents via [HandlesIntent] reflection. Path: /user/{actorSuffix}-{conversationId}. 250ms timeout;
        /// if not found creates via DependencyResolver with runtime type info. Enables plugin agents without compile-time knowledge.
        /// </summary>
        public async Task<IActorRef> GetOrCreateAgentAsync(Type agentType, string actorSuffix, string conversationId)
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