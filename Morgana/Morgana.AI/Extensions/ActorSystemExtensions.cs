using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.AI.Abstractions;

namespace Morgana.AI.Extensions;

/// <summary>Reaches a conversation's actors by name at <c>/user/{actorSuffix}-{conversationId}</c>, creating them through DI where none is running.</summary>
public static class ActorSystemExtensions
{
    extension(ActorSystem actorSystem)
    {
        /// <summary>Returns the conversation's running actor of type <typeparamref name="T"/>, creating it when none answers.</summary>
        public Task<IActorRef> GetOrCreateActorAsync<T>(string actorSuffix, string conversationId)
            where T : MorganaActor
            // The framework's own actors are reached exactly like a plugin's agent, by the type the caller names.
            => actorSystem.GetOrCreateAgentAsync(typeof(T), actorSuffix, conversationId);

        /// <summary>Returns the conversation's live actor at /user/{actorSuffix}-{conversationId}; null when none is running.</summary>
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
        /// Returns the conversation's running actor of <paramref name="agentType"/>, creating it when none answers.
        /// The type is known only at runtime because the router finds a plugin's agent by its <c>[HandlesIntent]</c>.
        /// </summary>
        public async Task<IActorRef> GetOrCreateAgentAsync(Type agentType, string actorSuffix, string conversationId)
        {
            // A later turn finds the actor an earlier turn created, so a conversation keeps one of each.
            if (await actorSystem.FindActorAsync(actorSuffix, conversationId) is { } runningActor)
                return runningActor;

            // None is running: the container builds it with the conversation id as its one argument of its own.
            Props actorProps = DependencyResolver.For(actorSystem).Props(agentType, conversationId);

            // The predictable name is what lets the next turn find it again.
            return actorSystem.ActorOf(actorProps, $"{actorSuffix}-{conversationId}");
        }
    }
}