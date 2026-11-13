using System.Globalization;
using System.Runtime.CompilerServices;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

// One Morgana host for the whole assembly: startup is expensive (plugin scan, agent registry,
// LLM tier validation) and nothing in the suite benefits from restarting it between classes.
[assembly: AssemblyFixture(typeof(MorganaHostFixture))]

// Strictly serial, for two reasons. The context-tool log lines carry no conversation id, so the turn
// observer correlates them by time window, which holds only while one turn is in flight. Every turn is
// also a live LLM call, so parallel scenarios would multiply the burst against the provider for little gain.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

/// <summary>Puts the whole suite on the invariant culture before any test or fixture code runs.</summary>
internal static class HarnessCulture
{
    /// <summary>
    /// Sets the invariant culture as Morgana.Web's own first statement does, which runs only once the fixture boots the host.
    /// A verdict, a report or a seeded row must read the same on a workstation set to it-IT and on one with no LANG at all.
    /// </summary>
    [ModuleInitializer]
    internal static void UseInvariantCulture()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}