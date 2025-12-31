using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Extensions;

public static class ActorSystemExtensions
{
    extension(ActorSystem actorSystem)
    {
        public async Task<IActorRef> GetOrCreateActor<T>(string actorSuffix, string conversationId)
            where T : MorganaActor
        {
            string actorName = $"{actorSuffix}-{conversationId}";

            try
            {
                return await actorSystem.ActorSelection($"/user/{actorName}")
                    .ResolveOne(TimeSpan.FromMilliseconds(250));
            }
            catch
            {
                Props props = DependencyResolver.For(actorSystem)
                    .Props<T>(conversationId);

                return actorSystem.ActorOf(props, actorName);
            }
        }

        public async Task<IActorRef> GetOrCreateAgent(Type agentType, string actorSuffix, string conversationId,
            IMCPToolProvider? mcpToolProvider=null)
        {
            string agentName = $"{actorSuffix}-{conversationId}";

            try
            {
                return await actorSystem.ActorSelection($"/user/{agentName}")
                    .ResolveOne(TimeSpan.FromMilliseconds(250));
            }
            catch
            {
                Props props = DependencyResolver.For(actorSystem)
                    .Props(agentType, conversationId, mcpToolProvider);

                return actorSystem.ActorOf(props, agentName);
            }
        }
    }
}