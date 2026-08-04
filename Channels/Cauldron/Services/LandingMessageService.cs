using Cauldron.Interfaces;

namespace Cauldron.Services;

/// <summary>
/// Supplies the whimsical line shown under the sparkle loader while Morgana warms up.
/// Registered as a singleton: the pool is read once and shared by every circuit.
/// </summary>
public class LandingMessageService : ILandingMessageService
{
    private readonly string[] landingMessages;
    private readonly Random random = new Random();

    public LandingMessageService(IConfiguration configuration)
    {
        // Falls back to a single hardcoded line so the loader is never left with nothing to say
        landingMessages = configuration.GetSection("Cauldron:LandingMessages").Get<string[]>()
                            ?? ["\uD83D\uDD2E Warming up the magic... almost there! \uD83D\uDD2E"];
    }

    /// <summary>
    /// Gets a random landing message from the pool of configured ones.
    /// </summary>
    public string GetLandingMessage()
    {
        // Random is not thread-safe and this singleton is shared across concurrent circuits,
        // so the lock keeps two simultaneous page loads from corrupting its internal state.
        lock (random)
        {
            return landingMessages[random.Next(landingMessages.Length)];
        }
    }
}