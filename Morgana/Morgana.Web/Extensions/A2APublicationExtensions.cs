using A2A.AspNetCore;
using Akka.Actor;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Morgana.AI.SessionStores;
using Morgana.Web.Filters;

namespace Morgana.Web.Extensions;

// Microsoft marks the A2A hosting run-mode API experimental (MEAI001). It is the documented way to
// declare that an agent answers inline rather than as a background task, which is exactly what a
// consultation is, so it is used deliberately — as MorganaAgentAdapter already does for IChatReducer.
#pragma warning disable MEAI001

/// <summary>
/// Publishes this installation's agents over A2A: the hosted agents before the container is built,
/// the endpoints and cards after.
/// </summary>
/// <remarks>
/// The chain is Microsoft's; Morgana contributes the hosted agent and its session store, both
/// reconciling one mismatch: A2A expects a long-lived agent per name, Morgana's are per-conversation
/// actors. The halves straddle <c>builder.Build()</c>, which is why they are named at all.
/// </remarks>
public static class A2APublicationExtensions
{
    /// <summary>
    /// Registers one hosted agent and A2A server per published intent.
    /// </summary>
    /// <remarks>
    /// An empty list registers nothing, which is what peer consultation switched off passes.
    /// </remarks>
    /// <param name="builder">Host builder, before its container is built.</param>
    /// <param name="publishedIntents">Agents to publish, already decided by the caller.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static WebApplicationBuilder AddMorganaA2A(this WebApplicationBuilder builder, IReadOnlyCollection<string> publishedIntents)
    {
        // Answering a colleague runs a real agent against a real model, so the wait is a turn's and
        // not a request timeout in disguise — but the innermost step of one: the desk that answers
        // gives up before the desk that asked, which gives up before the turn carrying them both.
        TimeSpan a2aRequestTimeout = Records.PeerConsultationWaits.From(builder.Configuration).Callee;

        // The session store has to know which system is asking and the hosting layer hands it only a
        // context id, so the request the gate proved is reached through the one thing that spans both.
        builder.Services.AddHttpContextAccessor();

        // How many conversations each admitted system may open, counted in a ledger of its own
        // because an issuer spans every conversation it opens.
        builder.Services.AddSingleton<IPeerAdmissionService, SQLitePeerAdmissionService>();

        foreach (string publishedIntent in publishedIntents)
        {
            builder.Services
                // One hosted agent per intent: it resolves the very actor the router would have reached and asks it a consultation.
                // Registered by intent name because that is the name an inboundA2A request arrives under.
                .AddAIAgent(
                    publishedIntent,
                    (serviceProvider, agentName) => new MorganaHostedAgent(
                        agentName,
                        serviceProvider.GetRequiredService<IAgentDirectoryService>().TryGetProjectedCard(agentName)?.Description ?? agentName,
                        serviceProvider.GetRequiredService<IAgentRegistryService>(),
                        serviceProvider.GetRequiredService<IPromptComposerService>(),
                        serviceProvider.GetRequiredService<ActorSystem>,
                        a2aRequestTimeout,
                        serviceProvider.GetRequiredService<IDustLimitService>(),
                        serviceProvider.GetRequiredService<IPeerAdmissionService>(),
                        serviceProvider.GetRequiredService<IConversationPersistenceService>(),
                        serviceProvider.GetRequiredService<ILogger>()))

                // The session store is where a request's A2A context id becomes a Morgana conversation.
                // Not isolation-key scoped: the context id IS the partition here and Morgana's own
                // per-conversation database already isolates everything the conversation owns.
                .WithSessionStore(
                    (serviceProvider, agentName) => new MorganaHostedAgentSessionStore(
                        // Who is asking, as the endpoint filter proved it on the request being served.
                        // Read at the moment a session is asked for rather than when the store is built,
                        // which is once for every request that will ever arrive.
                        () => serviceProvider.GetRequiredService<IHttpContextAccessor>()
                                             .HttpContext?.Items[A2AAuthenticationFilter.CallerIssuerItemKey] as string,
                        serviceProvider.GetRequiredService<ILogger>()),
                    ServiceLifetime.Singleton,
                    false)

                // Runs the agent inline and answers with a Message rather than a Task: a consultation is a
                // single question answered in full, which is what the published card advertises.
                .AddA2AServer(options => options.AgentRunMode = AgentRunMode.DisallowBackground);
        }

        // Handed back so this call can sit in a chain of registrations like every other Add* in the
        // host's manifest and for no other reason: nothing was replaced, only added to.
        return builder;
    }

    /// <summary>
    /// Maps one JSON-RPC endpoint and one well-known agent card per published agent and arranges for
    /// the cards to learn the address Kestrel ends up binding.
    /// </summary>
    /// <remarks>
    /// Runs on the built application, since it resolves the directory that projects the cards.
    /// </remarks>
    /// <param name="app">The built application, before it starts serving.</param>
    /// <param name="publishedIntents">The same agents handed to <see cref="AddMorganaA2A"/>.</param>
    public static async Task MapMorganaA2AAsync(this WebApplication app, IReadOnlyCollection<string> publishedIntents)
    {
        // No route for a host that published nothing, which is the other half of registering none:
        // such a deployment exposes no A2A surface at all, not an empty one that answers 404.
        if (publishedIntents.Count == 0)
            return;

        // Resolved once: this is the singleton holding the card cache and the one that later fills
        // those cards in with the address Kestrel bound.
        IAgentDirectoryService agentDirectory = app.Services.GetRequiredService<IAgentDirectoryService>();

        foreach (string publishedIntent in publishedIntents)
        {
            // Both maps below must agree on it: the endpoint sits here, its well-known card exactly
            // one level under, which is where a consumer of the published interface goes looking.
            string agentPath = $"{Constants.AgentToAgent.AgentPathPrefix}/{publishedIntent}";

            // No card means no intent by that name in the domain configuration: nothing to publish,
            // unreachable rather than broken. Startup already refused the incoherences that break.
            if (await agentDirectory.GetAgentCardAsync(publishedIntent) is null)
                continue;

            // These endpoints are the hosting layer's, not a controller's, so they carry no gate of
            // their own until one is put on them: A2AAuthenticationFilter is the same gate
            // MorganaController applies, narrowed to the systems this particular agent admits. The scope
            // is resolved here, once, so the gate enforces the very declaration startup validated.
            app.MapA2AJsonRpc(publishedIntent, agentPath)
               .AddEndpointFilter(new A2AAuthenticationFilter(
                   app.Services.GetRequiredService<IAuthenticationService>(),
                   publishedIntent,
                   ConfigurationAgentDirectoryService.ResolveAdmittedIssuers(app.Configuration, publishedIntent),
                   app.Services.GetRequiredService<ILogger>()));

            // Asked for on the request rather than handed over here. The card projected a moment ago
            // cannot yet name where this instance answers — Kestrel has bound nothing — and a document
            // completed later by mutating it would be read, by whoever asked in between, in whatever
            // state that pass had reached. Resolved per request there is no such moment.
            app.MapGet(
                $"{agentPath}/{Constants.AgentToAgent.WellKnownAgentCardPath}",
                async () => Results.Ok(await agentDirectory.GetAgentCardAsync(publishedIntent)));
        }
    }
}