using Morgana.Tests.NRT.Infrastructure;
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
