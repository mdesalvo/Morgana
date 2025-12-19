using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.AI.Abstractions;

namespace Morgana.AI.Extensions
{
    public static class ActorSystemExtensions
    {
        public static async Task<IActorRef> GetOrCreateActor<T>(this ActorSystem actorSystem,
            string actorSuffix, string conversationId) where T : MorganaActor
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

        public static async Task<IActorRef> GetOrCreateAgent(this ActorSystem actorSystem,
            Type agentType, string actorSuffix, string conversationId)
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
                    .Props(agentType, conversationId);

                return actorSystem.ActorOf(props, agentName);
            }
        }
    }
}