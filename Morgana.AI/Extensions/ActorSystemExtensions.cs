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
    }
}