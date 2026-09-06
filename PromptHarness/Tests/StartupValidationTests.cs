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
/// <para>Every case here breaks one <c>Morgana:AgentToAgent:Partners</c> entry against itself: what a
/// partner is, where it answers and how far it reaches are one declaration, so there is no second
/// list for a case to set against the first. This installation's own agents appear nowhere — they
/// consult each other under a key coined at startup, which no deployer can misdeclare.</para>
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
    public void Boot_is_refused_when_a_partner_has_no_usable_key()
    {
        // The one secret the two installations share. Left on the placeholder it is unconfigured
        // rather than secret and every call to or from that partner would fail on the wire instead.
        Exception refusal = AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__Partners__{fixture.ScopedPartnerIndex}__SymmetricKey", SecureOverride));

        Assert.Contains("SymmetricKey", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Boot_is_refused_when_a_partner_takes_the_name_of_this_installation()
    {
        // Reserved for the agents of this installation, whose consultations are proven against a
        // secret coined at startup. A partner taking it would be refused at runtime, for a reason
        // nothing in configuration shows.
        int reservedName = fixture.ScopedPartnerIndex + 1;

        Exception refusal = AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__Partners__{reservedName}__Name", "morgana"));

        Assert.Contains("reserved", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Boot_is_refused_when_a_partner_is_declared_twice()
    {
        // Which key proves a caller, and which address its calls go to, would be decided by the
        // order somebody happened to write the two entries in.
        int duplicate = fixture.ScopedPartnerIndex + 1;

        Exception refusal = AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__Partners__{duplicate}__Name", MorganaHostFixture.ScopedPartnerName),
            ($"Morgana__AgentToAgent__Partners__{duplicate}__SymmetricKey", fixture.ScopedPartnerKey));

        Assert.Contains(MorganaHostFixture.ScopedPartnerName, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_is_refused_when_a_partner_opens_neither_direction()
    {
        // An entry that reads as a live relationship and is none. Parking one is what "Enabled": false
        // says on the partner itself and it says it where a reader looks first.
        Exception refusal = AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__Partners__{fixture.ScopedPartnerIndex}__InboundPolicy__Enabled", "false"));

        Assert.Contains("neither direction", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Boot_is_refused_when_a_consultable_partner_declares_no_address()
    {
        // Where a token signed with that partner's key is sent. Without it the colleague resolves to
        // nothing on the first conversation instead of here.
        int addressless = fixture.ScopedPartnerIndex + 1;

        Exception refusal = AssertRefusesToBoot(
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
        int unmetered = fixture.ScopedPartnerIndex + 1;

        Exception refusal = AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__Partners__{unmetered}__Name", "harness-unmetered"),
            ($"Morgana__AgentToAgent__Partners__{unmetered}__SymmetricKey", fixture.ScopedPartnerKey),
            ($"Morgana__AgentToAgent__Partners__{unmetered}__InboundPolicy__Enabled", "true"));

        Assert.Contains("RateLimiting", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_is_refused_when_a_ceiling_is_switched_on_without_a_number()
    {
        // A ceiling that bounds nothing while reading as one that does.
        Exception refusal = AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__Partners__{fixture.ScopedPartnerIndex}__InboundPolicy__RateLimiting__MaxConversationsPerHour", "0"));

        Assert.Contains("MaxConversationsPerHour", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Boot_is_refused_when_a_partner_is_admitted_to_an_agent_nobody_publishes()
    {
        // A permission granted over nothing, most often a typo, read by whoever wrote it as real access.
        Exception refusal = AssertRefusesToBoot(
            ($"Morgana__AgentToAgent__Partners__{fixture.ScopedPartnerIndex}__InboundPolicy__OnAgents__0", "harness-nodesk"));

        Assert.Contains("harness-nodesk", refusal.Message, StringComparison.Ordinal);
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
