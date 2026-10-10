using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.AI.Abstractions;

namespace Morgana.AI.Extensions;

/// <summary>
/// Custom extensions for ActorSystem
/// </summary>
public static class ActorSystemExtensions
{
    extension(ActorSystem actorSystem)
    {
        /// <summary>
        /// Returns the conversation's actor of a known type at /user/{actorSuffix}-{conversationId}, creating it when absent.
        /// </summary>
        public async Task<IActorRef> GetOrCreateActorAsync<T>(string actorSuffix, string conversationId)
            where T : MorganaActor
        {
            // The name is the conversation's address for this role: one conversation owns at most one actor per suffix.
            string actorName = $"{actorSuffix}-{conversationId}";

            try
            {
                // A live actor is reused so that the conversation keeps the state that the actor holds.
                return await actorSystem.ActorSelection($"/user/{actorName}")
                    .ResolveOne(TimeSpan.FromMilliseconds(500));
            }
            catch
            {
                // Nothing answered at the path: the conversation has no such actor yet, so it is born here.
                // The resolver injects the actor's services and the conversation id is its only own argument.
                Props actorProps = DependencyResolver.For(actorSystem)
                    .Props<T>(conversationId);

                // The new actor is handed back so the caller can address it.
                return actorSystem.ActorOf(actorProps, actorName);
            }
        }

        /// <summary>
        /// Returns the conversation's live actor at /user/{actorSuffix}-{conversationId}; null when none is running.
        /// </summary>
        public async Task<IActorRef?> FindActorAsync(string actorSuffix, string conversationId)
        {
            try
            {
                // Only a running actor answers: a conversation whose actors have stopped has none, as does one never started in this process.
                return await actorSystem.ActorSelection($"/user/{actorSuffix}-{conversationId}")
                    .ResolveOne(TimeSpan.FromMilliseconds(500));
            }
            catch (ActorNotFoundException)
            {
                // No actor answers at the path, so the caller learns that there is none.
                return null;
            }
        }

        /// <summary>
        /// Returns the conversation's agent of a type known only at runtime at /user/{actorSuffix}-{conversationId}, creating it when absent.
        /// </summary>
        public async Task<IActorRef> GetOrCreateAgentAsync(Type agentType, string actorSuffix, string conversationId)
        {
            // The name is the conversation's address for this agent: one conversation owns at most one agent per suffix.
            string agentName = $"{actorSuffix}-{conversationId}";

            try
            {
                // A live agent is reused so that the conversation keeps the session that the agent holds.
                return await actorSystem.ActorSelection($"/user/{agentName}")
                    .ResolveOne(TimeSpan.FromMilliseconds(500));
            }
            catch
            {
                // Nothing answered at the path: the agent is born here from the type that [HandlesIntent] discovery found,
                // which is how a plugin's agent is created without the host knowing it at compile time.
                Props agentProps = DependencyResolver.For(actorSystem)
                    .Props(agentType, conversationId);

                // The agent actor is born from its type and handed back so the caller can address it.
                return actorSystem.ActorOf(agentProps, agentName);
            }
        }
    }
}