using Microsoft.Extensions.AI;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace PromptHarness.Infrastructure;

/// <summary>An MCP registry whose server lists the tools a test gives it; nothing is connected.</summary>
public sealed class ScriptedMCPClientRegistryService : IMCPClientRegistryService
{
    /// <summary>What the scripted server lists on each call; it may throw to model an unreachable server.</summary>
    private readonly Func<IList<AIFunction>> listing;

    /// <summary>How many times the server was listed.</summary>
    public int Listings { get; private set; }

    /// <summary>Creates a registry whose server lists whatever <paramref name="listing"/> returns.</summary>
    public ScriptedMCPClientRegistryService(Func<IList<AIFunction>> listing)
    {
        // The test decides what the server lists each time it is asked.
        this.listing = listing;
    }

    /// <summary>Lists the scripted tools and counts the listing.</summary>
    public Task<IList<AIFunction>> DiscoverResilientToolsAsync(UsesMCPServerAttribute serverAttribute, CancellationToken cancellationToken = default)
    {
        // Every listing is counted, including the one that throws.
        Listings++;
        return Task.FromResult(listing());
    }

    /// <summary>Nothing is held.</summary>
    public void Dispose()
    {
    }

    /// <summary>Nothing is held.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}