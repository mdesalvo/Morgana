using Microsoft.Extensions.Configuration;

namespace Morgana.Terminal.Services;

/// <summary>
/// Picks a random landing message from the channel's <c>LandingMessages</c> configured pool
/// — mirror of <c>Cauldron:LandingMessageService</c>. Used to print a friendly
/// "Morgana is warming up" line on stdout during the short startup window before the
/// Spectre.Console Live UI takes over: Kestrel bind, JWT signing, TLS handshake,
/// conversation-start handshake and the first webhook from Morgana together take
/// roughly 0.5–2 s and leaving the terminal empty for that stretch feels like a freeze.
/// </summary>
public sealed class LandingMessageService
{
    private readonly string[] landingMessages;
    private readonly Random random = new Random();

    /// <summary>Reads the channel's own pool of startup lines, falling back to a single built-in line.</summary>
    public LandingMessageService(IConfiguration configuration, ChannelProfile profile)
    {
        landingMessages = configuration.GetSection(profile.SectionKey("LandingMessages")).Get<string[]>()
            ?? ["🔮 Warming up the magic... almost there! 🔮"];
    }

    /// <summary>Returns a uniformly-random entry from the configured landing pool.</summary>
    public string GetLandingMessage()
    {
        lock (random)
        {
            return landingMessages[random.Next(landingMessages.Length)];
        }
    }
}
