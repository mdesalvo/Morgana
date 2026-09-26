using Morgana.Contracts;
using Morgana.Terminal;

namespace Rune.Messages;

/// <summary>
/// Rune's identity and capability budget: the "poor but honest" profile, every rich feature off and a
/// tight length cap, so Morgana's adapter rewrites the reply on every single turn — the degradation
/// path this channel exists to exercise.
/// </summary>
/// <remarks>
/// Identity stays channel-side rather than in the shared terminal library, which hosts the behaviour
/// the two TTY channels have in common and takes the profile as its only input. Raising anything here
/// is how Rune stops being worth running: the rewrite it proves would go untested everywhere.
/// </remarks>
public static class RuneChannelProfile
{
    /// <summary>Fallback cap advertised at the handshake when <c>Rune:MaxMessageLength</c> is absent.</summary>
    private const int DefaultMaxMessageLength = 500;

    /// <summary>
    /// Reads the advertised cap and builds the profile. The default stays aggressive so the downgrade
    /// runs on every turn, but can be raised or set to null, meaning "no cap", without recompiling.
    /// </summary>
    public static ChannelProfile Build(IConfiguration configuration) => new(
        ChannelName: "rune",
        DisplayName: "Rune",
        Capabilities: new ChannelCapabilities(
            SupportsRichCards: false,
            SupportsQuickReplies: false,
            SupportsStreaming: false,
            SupportsMarkdown: false,
            MaxMessageLength: configuration.GetValue<int?>("Rune:MaxMessageLength") ?? DefaultMaxMessageLength),

        // Rune offers no options to pick, so the newcomer is told to answer in words
        Introduction: "Talk with Morgana in plain text: write what you need and press Enter. She answers herself "
                      + "or hands you to the agent who knows the subject. Replies arrive whole and short: answer their questions in your own words.",
        KeyBindings:
        [
            ("Enter", "send"),
            ("↑↓ PgUp PgDn", "scroll back"),
            ("←→", "move the caret"),
            ("Esc", "leave")
        ]);
}
