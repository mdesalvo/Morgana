using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting that an incoherent trust declaration stops the instance at boot rather than
/// at the first consultation.
/// </summary>
/// <remarks>
/// <para>Kin to <c>AgentCardTests</c>: neither prose nor behaviour, deterministic and free. Where
/// that group reads a document a stranger fetches, this one reads what the instance refuses to
/// become. Both matter for the same reason — an A2A topology that boots cleanly can still be
/// unreachable, over-reaching or unmetered, none of which a running instance announces.</para>
///
/// <para>Every case boots the <b>real</b> entry point with one declaration broken, exactly as a
/// deployer would break it, so nothing here mocks a validator or asserts against one. The checks
/// live before the container is built, so a doomed host throws before it binds a port or raises an
/// actor system, which is what lets these run in the same process as the live host.</para>
///
/// <para>Almost every case here breaks one <c>Morgana:AgentToAgent:Partners</c> entry against itself:
/// what a partner is, where it answers and how far it reaches are one declaration, so there is no
/// second list for a case to set against the first. The exception is the address this installation
/// publishes itself at, which a proxied deployment declares because its binding cannot. Its own
/// agents appear nowhere — they consult each other under a key coined at startup, which no deployer
/// can misdeclare.</para>
///
/// <para>What configuration cannot reach is deliberately absent: a <c>[ConsultsAgent]</c> naming an
/// unknown colleague, one naming its own agent, two folding to one function name. Those are refused
/// at startup too, but they are declared in a plugin's code, so reaching them would need a second
/// plugin built to be wrong, which is the instrument of <c>PluginValidationTests</c>.</para>
/// </remarks>
public sealed class StartupValidationTests
{
    /// <summary>The live host, read for the positions this run's own declarations occupy.</summary>
    private readonly MorganaHostFixture fixture;

    public StartupValidationTests(MorganaHostFixture fixture) => this.fixture = fixture;

    /// <summary>Placeholder the shipped configuration carries where a real secret belongs.</summary>
    private const string SecureOverride = "_SECURE_OVERRIDE_";

    [Fact]
    public void Boot_is_refused_when_a_partner_has_no_usable_key()
    {
        // The one secret the two installations share. Left on the placeholder it is unconfigured
        // rather than secret and every call to or from that partner would fail on the wire instead.
        Exception refusal = DoomedHost.AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__Partners__{fixture.ScopedPartnerIndex}__SymmetricKey", SecureOverride));

        Assert.Contains("SymmetricKey", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Boot_is_refused_when_a_partner_key_is_too_short_to_sign_with()
    {
        // A key under the margin HMAC-SHA256 needs is refused here rather than where it is used: a
        // partner this installation only consults is proven by nobody at boot, so the first
        // consultation would be the one to discover it, mid-turn, inside an agent's tool call.
        Exception refusal = DoomedHost.AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__Partners__{fixture.ScopedPartnerIndex}__SymmetricKey", "too-short-to-sign"));

        Assert.Contains("256 bits", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Boot_is_refused_when_a_partner_takes_the_name_of_this_installation()
    {
        // Reserved for the agents of this installation, whose consultations are proven against a
        // secret coined at startup. A partner taking it would be refused at runtime, for a reason
        // nothing in configuration shows.
        int reservedName = fixture.FreePartnerIndex;

        Exception refusal = DoomedHost.AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__Partners__{reservedName}__Name", "morgana"));

        Assert.Contains("reserved", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Boot_is_refused_when_a_partner_is_declared_twice()
    {
        // The order somebody happened to write the two entries in would decide which key proves a
        // caller and which address its calls go to.
        int duplicate = fixture.FreePartnerIndex;

        Exception refusal = DoomedHost.AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__Partners__{duplicate}__Name", MorganaHostFixture.ScopedPartnerName),
            ($"Morgana__AgentToAgent__Partners__{duplicate}__SymmetricKey", fixture.ScopedPartnerKey));

        Assert.Contains(MorganaHostFixture.ScopedPartnerName, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_is_refused_when_a_partner_opens_neither_direction()
    {
        // An entry that reads as a live relationship and is none. Parking one is what "Enabled": false
        // says on the partner itself and it says it where a reader looks first.
        Exception refusal = DoomedHost.AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__Partners__{fixture.ScopedPartnerIndex}__InboundPolicy__Enabled", "false"));

        Assert.Contains("neither direction", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Boot_is_refused_when_a_consultable_partner_declares_no_address()
    {
        // Where a token signed with that partner's key is sent. Without it the colleague resolves to
        // nothing on the first conversation instead of here.
        int addressless = fixture.FreePartnerIndex;

        Exception refusal = DoomedHost.AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__Partners__{addressless}__Name", "harness-addressless"),
            ($"Morgana__AgentToAgent__Partners__{addressless}__SymmetricKey", fixture.ScopedPartnerKey),
            ($"Morgana__AgentToAgent__Partners__{addressless}__OutboundPolicy__Enabled", "true"));

        Assert.Contains("Url", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_is_refused_when_an_admitted_partner_declares_no_rate_limiting()
    {
        // Behind the A2A door the caller names the conversation it is served on, so how many it may
        // open is the only bound on what it can spend. An absent declaration is not licence to spend
        // freely — a deployment wanting no bound switches the ceiling off in as many words.
        int unmetered = fixture.FreePartnerIndex;

        Exception refusal = DoomedHost.AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__Partners__{unmetered}__Name", "harness-unmetered"),
            ($"Morgana__AgentToAgent__Partners__{unmetered}__SymmetricKey", fixture.ScopedPartnerKey),
            ($"Morgana__AgentToAgent__Partners__{unmetered}__InboundPolicy__Enabled", "true"));

        Assert.Contains("RateLimiting", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_is_refused_when_a_ceiling_is_switched_on_without_a_number()
    {
        // A ceiling that bounds nothing while reading as one that does.
        Exception refusal = DoomedHost.AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__Partners__{fixture.ScopedPartnerIndex}__InboundPolicy__RateLimiting__MaxConversationsPerHour", "0"));

        Assert.Contains("MaxConversationsPerHour", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_is_refused_when_a_partner_is_admitted_to_an_agent_nobody_publishes()
    {
        // A permission granted over nothing, most often a typo, read by whoever wrote it as real access.
        Exception refusal = DoomedHost.AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__Partners__{fixture.ScopedPartnerIndex}__InboundPolicy__OnAgents__0", "harness-noagent"));

        Assert.Contains("harness-noagent", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_is_refused_when_this_installation_declares_an_address_no_peer_could_reach()
    {
        // The only declaration here that is about this installation rather than a partner and it is
        // made only when the binding cannot answer for it. Wrong, every card published carries it and
        // every consumer refuses the colleague behind it, for a reason no running instance announces.
        Exception refusal = DoomedHost.AssertRefusesToBoot(
            ("Morgana__AgentToAgent__PublicUrl", "morgana.example.com"));

        Assert.Contains("PublicUrl", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_is_refused_when_a_tier_has_no_provider()
    {
        // Nothing else resolves the LLM service before a turn does, so a tier left unusable would reach
        // a user as a conversation that never answers instead of stopping the boot.
        Exception refusal = DoomedHost.AssertRefusesToBoot(
            ("Morgana__LanguageModels__Tiers__Performance__Provider", string.Empty));

        Assert.Contains("Morgana:LanguageModels:Tiers:Performance:Provider", refusal.Message, StringComparison.Ordinal);
    }
}
