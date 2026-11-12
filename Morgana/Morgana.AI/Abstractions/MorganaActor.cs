using System.Globalization;
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Abstractions;

/// <summary>Base of every conversation-scoped actor of Morgana, the domain agents included.</summary>
public class MorganaActor : ReceiveActor
{
    /// <summary>The conversation this actor serves, part of its name and of every log line.</summary>
    protected readonly string conversationId;

    /// <summary>The language models, on the tier each caller is served by.</summary>
    protected readonly ILanguageModelService llmService;

    /// <summary>Resolves the prompts of morgana.json and agents.json by id.</summary>
    protected readonly IPromptResolverService promptResolverService;

    /// <summary>The actor's logger, which stamps its path on every line.</summary>
    protected readonly ILoggingAdapter actorLogger;

    /// <summary>The host's configuration as ASP.NET layers it.</summary>
    protected readonly IConfiguration configuration;

    /// <summary>
    /// How long an actor waits on silence, as <c>Morgana:ActorSystem:TimeoutSeconds</c> declares it.
    /// Read on every use, so a configuration reload reaches the next wait without restarting the actor.
    /// </summary>
    protected TimeSpan ActorSystemTimeout =>
        TimeSpan.FromSeconds(Convert.ToInt32(configuration["Morgana:ActorSystem:TimeoutSeconds"], CultureInfo.InvariantCulture));

    /// <summary>Binds the actor to its conversation and to the services every actor reads.</summary>
    protected MorganaActor(
        string conversationId,
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IConfiguration configuration)
    {
        this.conversationId = conversationId;
        this.llmService = llmService;
        this.promptResolverService = promptResolverService;
        this.configuration = configuration;
        actorLogger = Context.GetLogger();

        // Every actor starts on the same budget of silence and handles its expiry, so the timeout never ends up in dead letters.
        SetReceiveTimeout(ActorSystemTimeout);
        Receive<ReceiveTimeout>(HandleReceiveTimeout);
    }

    /// <summary>
    /// Reacts to a silence longer than the actor's receive timeout; by default it does nothing.
    /// An idle actor is legitimate, so the default writes nothing to keep the logs free of it.
    /// </summary>
    protected virtual void HandleReceiveTimeout(ReceiveTimeout timeout)
    {
    }

    /// <summary>
    /// Registers the handlers that every behaviour of the actor needs, called again after each <c>Become</c>.
    /// Become replaces every handler, the timeout's included, so a state that forgot it would send the timeout to dead letters.
    /// </summary>
    protected virtual void RegisterCommonHandlers()
    {
        // The timeout is handled in every state as it is at construction.
        Receive<ReceiveTimeout>(HandleReceiveTimeout);
    }
}