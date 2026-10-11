using Microsoft.Extensions.Configuration;

namespace Morgana.Terminal.Services;

/// <summary>
/// Picks a random landing message from the channel's configured pool, as Cauldron's landing service does. It gives the
/// terminal a "Morgana is warming up" line during the 0.5 to 2 seconds before the live UI takes over (Kestrel bind, JWT
/// signing, TLS and conversation handshakes, the first webhook), since an empty terminal for that stretch feels like a freeze.
/// </summary>
public sealed class LandingMessageService
{
    /// <summary>The pool the startup line is drawn from.</summary>
    private readonly string[] landingMessages;

    /// <summary>Picks the entry, shared by every caller and therefore locked.</summary>
    private readonly Random random = new();

    /// <summary>Reads the channel's own pool of startup lines, falling back to a single built-in line.</summary>
    public LandingMessageService(IConfiguration configuration, ChannelProfile profile)
    {
        // A channel with no pool configured still says something while it warms up
        landingMessages = configuration.GetSection(profile.SectionKey("LandingMessages")).Get<string[]>()
            ?? ["🔮 Warming up the magic... almost there! 🔮"];
    }

    /// <summary>Returns a uniformly random entry of the landing pool.</summary>
    public string GetLandingMessage()
    {
        // The generator is not safe to share between threads
        lock (random)
        {
            // Any entry is as good as another
            return landingMessages[random.Next(landingMessages.Length)];
        }
    }
}
