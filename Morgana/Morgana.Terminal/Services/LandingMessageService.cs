using Microsoft.Extensions.Configuration;

namespace Morgana.Terminal.Services;

/// <summary>Picks a random startup line from the channel's configured pool, as Cauldron's landing service does.</summary>
// The line covers the 0.5 to 2 seconds before the live UI takes over (Kestrel bind, token signing, TLS and conversation
// handshakes, the first webhook), since an empty terminal for that stretch feels like a freeze.
public sealed class LandingMessageService
{
    /// <summary>The pool the startup line is drawn from.</summary>
    private readonly string[] landingMessages;

    /// <summary>Reads the channel's own pool of startup lines, falling back to a single built-in line.</summary>
    public LandingMessageService(IConfiguration configuration, ChannelProfile profile)
    {
        // A channel with no pool configured still says something while it warms up.
        landingMessages = configuration.GetSection(profile.SectionKey("LandingMessages")).Get<string[]>()
            ?? ["🔮 Warming up the magic... almost there! 🔮"];
    }

    /// <summary>A uniformly random entry of the landing pool.</summary>
    public string GetLandingMessage()
    {
        // Any entry is as good as another; the shared generator is safe to call from any thread.
        return landingMessages[Random.Shared.Next(landingMessages.Length)];
    }
}