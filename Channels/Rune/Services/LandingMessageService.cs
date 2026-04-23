namespace Rune.Services;

/// <summary>
/// Picks a random landing message from the <c>Rune:LandingMessages</c> configured pool
/// — mirror of <c>Rune:LandingMessageService</c>. Used to print a friendly
/// "Morgana is warming up" line on stdout during the short startup window before the
/// Spectre.Console Live UI takes over: Kestrel bind, JWT signing, TLS handshake,
/// conversation-start handshake and the first webhook from Morgana together take
/// roughly 0.5–2 s, and leaving the terminal empty for that stretch feels like a freeze.
/// </summary>
/// <remarks>
/// <para>The pool falls back to a single canned message if the configuration section is
/// absent, so a freshly-cloned repo without <c>Rune:LandingMessages</c> still renders a
/// sensible line. Picking is done under a lock around a shared <see cref="Random"/> —
/// the method is called once per process lifetime today, but the cost is negligible and
/// keeps the service safe if ever reused from concurrent contexts.</para>
/// </remarks>
public sealed class LandingMessageService
{
    private readonly string[] landingMessages;
    private readonly Random random = new Random();

    public LandingMessageService(IConfiguration configuration)
    {
        landingMessages = configuration.GetSection("Rune:LandingMessages").Get<string[]>()
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
