using System.Globalization;
using System.Runtime.CompilerServices;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

// One Morgana host for the whole assembly: startup is expensive (plugin scan, agent registry,
// LLM tier validation) and nothing in the suite benefits from restarting it between classes.
[assembly: AssemblyFixture(typeof(MorganaHostFixture))]

// Strictly serial. Two reasons, both load-bearing:
//   1. The context-tool log lines carry no conversation id, so the turn observer correlates them
//      by time window — which is only sound while a single turn is in flight at a time.
//   2. Every turn is a live LLM call; running scenarios in parallel would multiply the burst rate
//      against the provider without shortening the suite by much.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

/// <summary>Puts the whole suite on the invariant culture before any test or fixture code runs.</summary>
internal static class HarnessCulture
{
    /// <summary>
    /// Mirrors Morgana.Web's own first statement, which only runs once the fixture boots the host: a verdict,
    /// a report or a seeded row must read the same on a workstation set to it-IT and on one with no LANG at all.
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
