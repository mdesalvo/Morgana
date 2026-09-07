using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using A2A;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.A2A;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting what this installation does when a partner consults it: which conversation the
/// exchange is served on, what the answer costs and how many exchanges a partner may open.
/// </summary>
/// <remarks>
/// <para>The counterpart of <c>PeerFederationTests</c>, which reads what leaves toward a colleague
/// published elsewhere. Here this installation is the one answering, so the partner is the harness
/// itself: two are declared on its own run, one admitted to a single desk and one allowed a single
/// exchange an hour.</para>
///
/// <para>The question is put through the A2A client rather than as a hand-written envelope, so what
/// knocks is what a partner's own Morgana would send. What comes back is read as the serialized
/// envelope it is — an answer, whether the colleague awaits a reply and what the turn cost — never as
/// prose to judge: a consulted desk's wording is <c>ConsultingTests</c>' subject, not this group's.</para>
///
/// <para><b>These turns cost.</b> An admitted request reaches a real agent and a real model; only the
/// refusal at the end costs nothing, being decided before any desk is troubled. Everything asserted
/// here is nonetheless deterministic — a conversation's name, a number's presence, a sentence a
/// deployment wrote itself. What a consultation costs in dust is deliberately not among them: dust
/// limiting is off in this run as in every group but <c>DustTests</c>, so the figure reported back is
/// honestly zero and only the decision to report it at all belongs here.</para>
/// </remarks>
public sealed class ServedConsultationTests
{
    /// <summary>The live host, consulted here the way a partner would consult it.</summary>
    private readonly MorganaHostFixture fixture;

    public ServedConsultationTests(MorganaHostFixture fixture) => this.fixture = fixture;

    /// <summary>Desk on the asking side, declared so the answer reports what it cost.</summary>
    private const string CallerIntent = "billing";

    /// <summary>
    /// How a consultation names the desk that asked. Spelled out rather than read from the framework:
    /// it travels as protocol metadata between two installations that share no code.
    /// </summary>
    private const string CallerIntentMetadataKey = "morgana:caller";

    /// <summary>
    /// What parts a partner's name from the conversation it wrote. Spelled out for the same reason:
    /// the shape of a name kept apart is the thing under test, not a constant compared to itself.
    /// </summary>
    private const string ForeignConversationSeparator = "~";

    [Fact]
    public async Task A_partner_is_served_on_a_conversation_of_its_own_and_never_on_the_one_it_named()
    {
        // The name of a conversation a user could be having right now. Behind the A2A door it is a
        // string a stranger wrote: honoured as ours it would reach that user's agents, read the shared
        // context they were told and spend their budget.
        string namedConversation = $"live-conversation-{Guid.NewGuid():N}";

        PeerEnvelope envelope = await ConsultAsync(
            MorganaHostFixture.ScopedPartnerName, fixture.ScopedPartnerKey, namedConversation, CallerIntent);

        // A desk that answered at all, which is what makes the rest of this test about where it answered.
        Assert.False(string.IsNullOrWhiteSpace(envelope.Answer));

        // The exchange lives under the issuer the gate proved, which is the one thing this caller
        // cannot choose. The name it wrote opens nothing.
        Assert.True(File.Exists(ConversationDatabase($"{MorganaHostFixture.ScopedPartnerName}{ForeignConversationSeparator}{namedConversation}")));
        Assert.False(File.Exists(ConversationDatabase(namedConversation)));
    }

    [Fact]
    public async Task What_an_answer_cost_travels_back_to_a_caller_that_declared_itself_an_agent()
    {
        // The tokens a consultation burns are burned here, on the answering installation, and would
        // otherwise be invisible to the budget that is supposed to say what a conversation cost.
        PeerEnvelope envelope = await ConsultAsync(
            MorganaHostFixture.ScopedPartnerName, fixture.ScopedPartnerKey, $"costed-{Guid.NewGuid():N}", CallerIntent);

        // That a figure is reported at all, never what it comes to: this run leaves dust limiting off,
        // as every group but DustTests does, so nothing is charged and the honest figure is zero. What
        // is under test is who the figure is put on the envelope for.
        Assert.NotNull(envelope.DustConsumed);
    }

    [Fact]
    public async Task What_an_answer_cost_stays_here_when_the_caller_never_declared_itself()
    {
        // Anything else that speaks A2A has no ledger to charge the figure to and would carry a number
        // it cannot read. Unreported, the spend simply stays on this installation's own books.
        PeerEnvelope envelope = await ConsultAsync(
            MorganaHostFixture.ScopedPartnerName, fixture.ScopedPartnerKey, $"undeclared-{Guid.NewGuid():N}", callerIntent: null);

        Assert.Null(envelope.DustConsumed);
    }

    [Fact]
    public async Task A_partner_that_has_opened_its_hour_is_turned_away_in_this_deployment_voice()
    {
        // A caller behind this door writes the name of the conversation it is served on, so a partner
        // rotating names would draw a fresh budget with every one. What is bounded is therefore how
        // many exchanges may start, and this partner's entry allows exactly one an hour.
        PeerEnvelope admitted = await ConsultAsync(
            MorganaHostFixture.MeteredPartnerName, fixture.MeteredPartnerKey, $"first-{Guid.NewGuid():N}", CallerIntent);

        Assert.False(string.IsNullOrWhiteSpace(admitted.Answer));

        PeerEnvelope refused = await ConsultAsync(
            MorganaHostFixture.MeteredPartnerName, fixture.MeteredPartnerKey, $"second-{Guid.NewGuid():N}", CallerIntent);

        // The sentence is the deployment's own, written on that partner's entry: a turned-away
        // colleague reads something its asking model can act on, never a status code to narrate.
        Assert.Equal(MorganaHostFixture.MeteredPartnerRefusal, refused.Answer);

        // Refused before any desk was troubled, so there is nothing to report the cost of.
        Assert.Null(refused.DustConsumed);
    }

    /// <summary>
    /// Consults one published agent as a partner would, handing back the envelope it answered with.
    /// </summary>
    /// <param name="partnerName">Partner to sign as, which is what the gate reads.</param>
    /// <param name="symmetricKey">Key that partner is declared with on the host under test.</param>
    /// <param name="conversationName">The A2A context id, written by the caller exactly as a partner writes one.</param>
    /// <param name="callerIntent">Asking desk, or <c>null</c> for a caller that is not an agent of a Morgana.</param>
    private async Task<PeerEnvelope> ConsultAsync(string partnerName, string symmetricKey, string conversationName, string? callerIntent)
    {
        using HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintToken(partnerName, symmetricKey));

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
            "Which plants are in stock right now?", session, options, TestContext.Current.CancellationToken);

        return JsonSerializer.Deserialize<PeerEnvelope>(response.Text, EnvelopeFormat)
            ?? throw new InvalidOperationException($"The consultation answered something that is not an envelope: {response.Text}");
    }

    /// <summary>Mints the token a partner holding that key would sign its own calls with.</summary>
    /// <param name="partnerName">Issuer to sign under.</param>
    /// <param name="symmetricKey">Key the two installations share.</param>
    private string MintToken(string partnerName, string symmetricKey)
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = partnerName,
            Audience = fixture.Configuration["Morgana:Authentication:Audience"],
            Subject = new ClaimsIdentity([new Claim("sub", partnerName)]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(symmetricKey)), SecurityAlgorithms.HmacSha256)
        });

    /// <summary>Where the database of one conversation of this run would be, whether or not it exists.</summary>
    /// <param name="conversationId">Conversation whose file is being looked for.</param>
    private string ConversationDatabase(string conversationId)
        => Path.Combine(fixture.StoragePath, $"morgana-{conversationId}.db");

    /// <summary>Reads the envelope whatever casing the answering side serialized its fields under.</summary>
    private static readonly JsonSerializerOptions EnvelopeFormat = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// What a consultation answers with, as the asking side reads it: data to act on rather than
    /// prose to relay.
    /// </summary>
    /// <param name="Answer">What the consulted desk said.</param>
    /// <param name="AwaitingReply">Whether it expects the exchange to continue.</param>
    /// <param name="DustConsumed">What the turn cost, present only for a caller that declared itself an agent.</param>
    private sealed record PeerEnvelope(
        [property: JsonPropertyName("answer")] string Answer,
        [property: JsonPropertyName("awaitingReply")] bool AwaitingReply,
        [property: JsonPropertyName("dustConsumed")] double? DustConsumed);
}
