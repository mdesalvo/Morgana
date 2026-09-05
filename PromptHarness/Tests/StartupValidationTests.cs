using System.Reflection;
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
/// <para>What configuration cannot reach is deliberately absent: a <c>[ConsultsAgent]</c> naming an
/// unknown colleague, one naming its own agent, two folding to one function name. Those are refused
/// at startup too, but they are declared in a plugin's code, so reaching them would need a second
/// plugin built to be wrong — a different instrument from this one.</para>
/// </remarks>
public sealed class StartupValidationTests
{
    /// <summary>The live host, read for the positions this run's own declarations occupy.</summary>
    private readonly MorganaHostFixture fixture;

    public StartupValidationTests(MorganaHostFixture fixture) => this.fixture = fixture;

    /// <summary>
    /// How long a doomed host is given to refuse. The checks run before anything is bound, so a
    /// boot that is still alive after this did not refuse at all.
    /// </summary>
    private static readonly TimeSpan RefusalBudget = TimeSpan.FromSeconds(60);

    /// <summary>Placeholder the shipped configuration carries where a real secret belongs.</summary>
    private const string SecureOverride = "_SECURE_OVERRIDE_";

    [Fact]
    public void Boot_is_refused_when_the_peer_issuer_has_no_usable_key()
    {
        // The key an installation signs its own consultations with. Left on the placeholder it is
        // unconfigured rather than secret. A colleague would resolve to nothing on the first
        // conversation instead of here.
        Exception refusal = AssertRefusesToBoot(
            ($"Morgana__Authentication__Issuers__{fixture.PeerIssuerIndex}__SymmetricKey", SecureOverride));

        Assert.Contains("SymmetricKey", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Boot_is_refused_when_the_peer_issuer_is_typed_as_a_channel()
    {
        // Signed under an issuer its own A2A door turns away: the ring would be configured, signed
        // and refused by the instance that raised it.
        Exception refusal = AssertRefusesToBoot(
            ($"Morgana__Authentication__Issuers__{fixture.PeerIssuerIndex}__Type", "channel"));

        Assert.Contains("system", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Boot_is_refused_when_the_peer_issuer_is_admitted_nowhere()
    {
        // Proving who you are is not being admitted. With its own issuer absent from the inbound
        // declarations the instance would answer its own consultations with a 401.
        Exception refusal = AssertRefusesToBoot(
            ("Morgana__AgentToAgent__InboundSystems__0__Issuer", MorganaHostFixture.ScopedSystemIssuerName));

        Assert.Contains("InboundSystems", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_is_refused_when_a_system_issuer_reaches_nothing()
    {
        // A system declared then forgotten in the inbound list would be refused at every agent for
        // a reason nobody wrote down. Silence is the dangerous direction, so it is not allowed.
        int orphanIssuer = fixture.ScopedSystemIssuerIndex + 1;

        Exception refusal = AssertRefusesToBoot(
            ($"Morgana__Authentication__Issuers__{orphanIssuer}__Name", "harness-orphan"),
            ($"Morgana__Authentication__Issuers__{orphanIssuer}__SymmetricKey", fixture.ScopedSystemKey),
            ($"Morgana__Authentication__Issuers__{orphanIssuer}__Type", "system"));

        Assert.Contains("harness-orphan", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_is_refused_when_a_scope_names_an_issuer_nobody_declared()
    {
        // Scoping narrows a caller that can already prove who it is. A name absent from the issuers
        // describes an admission that can never happen.
        int strayScope = fixture.ScopedSystemInboundIndex + 1;

        Exception refusal = AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__InboundSystems__{strayScope}__Issuer", "harness-unknown"),
            ($"Morgana__AgentToAgent__InboundSystems__{strayScope}__MaxConversationsPerHour", "10"));

        Assert.Contains("harness-unknown", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_is_refused_when_a_scope_names_a_channel()
    {
        // A caller is a channel or a colleague, never both: a channel's key opens the conversation
        // API and nothing under the published agents, so listing which of them it reaches is a
        // reach that key can never have.
        int channelScope = fixture.ScopedSystemInboundIndex + 1;

        Exception refusal = AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__InboundSystems__{channelScope}__Issuer", "cauldron"),
            ($"Morgana__AgentToAgent__InboundSystems__{channelScope}__MaxConversationsPerHour", "10"));

        Assert.Contains("channel", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Boot_is_refused_when_the_peer_issuer_carries_a_scope()
    {
        // Which colleagues an agent of this installation may consult has one author, the attribute
        // in its code. A scope here could only contradict it, at runtime, as a 401.
        Exception refusal = AssertRefusesToBoot(
            ("Morgana__AgentToAgent__InboundSystems__0__Agents__0", MorganaHostFixture.ScopedSystemAgent));

        Assert.Contains("Agents", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_is_refused_when_the_peer_issuer_carries_a_ceiling()
    {
        // A colleague of this installation opens no conversation, it joins the one the user is
        // already having, so a ceiling on openings would count nothing while reading as one that does.
        Exception refusal = AssertRefusesToBoot(
            ("Morgana__AgentToAgent__InboundSystems__0__MaxConversationsPerHour", "10"));

        Assert.Contains("MaxConversationsPerHour", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_is_refused_when_an_admitted_system_has_no_ceiling()
    {
        // Behind the A2A door the caller names the conversation it is served on, so how many it may
        // open is the only bound on what it can spend. An absent key is not licence to spend freely.
        int uncappedPartner = fixture.ScopedSystemInboundIndex + 1;

        Exception refusal = AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__InboundSystems__{uncappedPartner}__Issuer", MorganaHostFixture.ScopedSystemIssuerName),
            ($"Morgana__AgentToAgent__InboundSystems__{uncappedPartner}__Agents__0", MorganaHostFixture.ScopedSystemAgent));

        Assert.Contains("MaxConversationsPerHour", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_is_refused_when_a_scope_names_an_agent_nobody_publishes()
    {
        // A permission granted over nothing, most often a typo, read by whoever wrote it as real access.
        Exception refusal = AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__InboundSystems__{fixture.ScopedSystemInboundIndex}__Agents__0", "harness-nodesk"));

        Assert.Contains("harness-nodesk", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_is_refused_when_an_issuer_declares_no_type()
    {
        // Type decides which door a key opens. Defaulting it would classify a caller by enum ordering —
        // a classification that decides whether a key reaches an agent's actor at all.
        int untypedIssuer = fixture.ScopedSystemIssuerIndex + 1;

        Exception refusal = AssertRefusesToBoot(
            ($"Morgana__Authentication__Issuers__{untypedIssuer}__Name", "harness-untyped"),
            ($"Morgana__Authentication__Issuers__{untypedIssuer}__SymmetricKey", fixture.ScopedSystemKey));

        Assert.Contains("Type", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Boots the host with the given declarations replaced, expecting it to refuse.
    /// </summary>
    /// <remarks>
    /// The overrides are applied to this process's environment, which is how the host reads its
    /// configuration, then put back whatever the outcome: the live host beside it read its own
    /// configuration when it started, so what moves here reaches only the boot about to happen.
    /// Test classes in this assembly never run at the same time, which is what makes that safe.
    /// </remarks>
    /// <param name="overrides">Configuration keys to replace, in environment-variable form.</param>
    /// <returns>The exception the host refused with.</returns>
    private static Exception AssertRefusesToBoot(params (string Key, string Value)[] overrides)
    {
        (string Key, string? Value)[] restore =
            [.. overrides.Select(entry => (entry.Key, Environment.GetEnvironmentVariable(entry.Key)))];

        foreach ((string key, string value) in overrides)
            Environment.SetEnvironmentVariable(key, value);

        try
        {
            Exception? refusal = BootAndWaitForRefusal();

            Assert.True(refusal is not null,
                "The host booted a trust configuration it was supposed to refuse: "
                + string.Join(", ", overrides.Select(entry => $"{entry.Key}={entry.Value}")));

            return refusal!;
        }
        finally
        {
            foreach ((string key, string? value) in restore)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>
    /// Runs the host's entry point on a thread of its own, handing back what it threw.
    /// </summary>
    /// <remarks>
    /// A refusal is fatal to the boot, so it arrives as an exception rather than a status. A boot
    /// that does not refuse would serve forever, which is why the wait is bounded rather than
    /// joined: the port is left to the server to pick, so nothing collides with the live host if
    /// this one does start.
    /// </remarks>
    /// <returns>What the boot threw, or <c>null</c> when it was still running when time ran out.</returns>
    private static Exception? BootAndWaitForRefusal()
    {
        MethodInfo entryPoint = typeof(Program).Assembly.EntryPoint
            ?? throw new InvalidOperationException("Morgana.Web exposes no entry point.");

        string[] arguments =
        [
            "--urls", "http://127.0.0.1:0",
            "--environment", "Development",
            "--contentRoot", AppContext.BaseDirectory,
            "--applicationName", typeof(Program).Assembly.GetName().Name!
        ];

        Exception? thrown = null;

        Thread boot = new Thread(() =>
        {
            try
            {
                if (entryPoint.Invoke(null, [arguments]) is Task hostTask)
                    hostTask.GetAwaiter().GetResult();
            }
            catch (TargetInvocationException ex)
            {
                // Reflection wraps what the boot threw; the refusal itself is what a deployer reads.
                thrown = ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        })
        {
            IsBackground = true,
            Name = "morgana-harness-doomed-host"
        };

        boot.Start();
        boot.Join(RefusalBudget);

        return thrown;
    }
}
