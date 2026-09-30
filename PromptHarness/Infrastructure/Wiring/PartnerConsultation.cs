using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using A2A;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.A2A;
using Xunit;

namespace PromptHarness.Infrastructure.Wiring;

/// <summary>
/// A partner consulting one published agent of the host under test: signed as that partner, bound to
/// the interface the agent's card advertises and reading back the envelope it answers with.
/// </summary>
/// <remarks>
/// The question is put through the A2A client rather than as a hand-written envelope, so what knocks is
/// what a partner's own Morgana would send.
/// </remarks>
/// <param name="fixture">The live host, whose address and partner keys every consultation uses.</param>
public sealed class PartnerConsultation(MorganaHostFixture fixture)
{
    /// <summary>
    /// How a consultation names the agent that asked. Spelled out rather than read from the framework:
    /// it travels as protocol metadata between two installations that share no code.
    /// </summary>
    private const string CallerIntentMetadataKey = "morgana:caller";

    /// <summary>Reads the envelope whatever casing the answering side serialized its fields under.</summary>
    private static readonly JsonSerializerOptions EnvelopeFormat = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Consults the agent the scoped partner is admitted to, handing back the envelope it answered with.
    /// </summary>
    /// <param name="partnerName">Partner to sign as, which is what the gate reads.</param>
    /// <param name="symmetricKey">Key that partner is declared with on the host under test.</param>
    /// <param name="conversationName">The A2A context id, written by the caller exactly as a partner writes one.</param>
    /// <param name="callerIntent">Asking agent, or <c>null</c> for a caller that is not an agent of a Morgana.</param>
    /// <param name="question">What to ask, defaulting to an ordinary one this agent answers for.</param>
    public async Task<PeerEnvelope> ConsultAsync(
        string partnerName, string symmetricKey, string conversationName, string? callerIntent,
        string question = "Which plants are in stock right now?")
    {
        using HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new ChannelApiClient(fixture).MintToken(partnerName, symmetricKey));

        // The card is read first and the client is bound to the interface it advertises, which is the
        // whole of how a partner learns where to knock — the path is never assembled from an assumption.
        AgentCard card = await new A2ACardResolver(
            new Uri($"{fixture.BaseAddress}/a2a/{MorganaHostFixture.ScopedPartnerAgent}/"), httpClient)
            .GetAgentCardAsync(TestContext.Current.CancellationToken);

        AIAgent colleague = card.AsAIAgent(httpClient);

        AgentSession session = colleague is A2AAgent a2aColleague
            ? await a2aColleague.CreateSessionAsync(conversationName)
            : await colleague.CreateSessionAsync(TestContext.Current.CancellationToken);

        AgentRunOptions options = new AgentRunOptions();
        if (callerIntent is not null)
        {
            options.AdditionalProperties ??= [];
            options.AdditionalProperties[CallerIntentMetadataKey] = callerIntent;
        }

        AgentResponse response = await colleague.RunAsync(
            question, session, options, TestContext.Current.CancellationToken);

        return JsonSerializer.Deserialize<PeerEnvelope>(response.Text, EnvelopeFormat)
            ?? throw new InvalidOperationException($"The consultation answered something that is not an envelope: {response.Text}");
    }
}

/// <summary>
/// What a consultation answers with, as the asking side reads it: data to act on rather than prose
/// to relay.
/// </summary>
/// <param name="Answer">What the consulted agent said.</param>
/// <param name="AwaitingReply">Whether it expects the exchange to continue.</param>
/// <param name="DustConsumed">What the turn cost, present only for a caller that declared itself an agent.</param>
public sealed record PeerEnvelope(
    [property: JsonPropertyName("answer")] string Answer,
    [property: JsonPropertyName("awaitingReply")] bool AwaitingReply,
    [property: JsonPropertyName("dustConsumed")] double? DustConsumed);
