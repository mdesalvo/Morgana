using Morgana.Contracts;
using Morgana.Terminal;

namespace Grimoire.Messages;

/// <summary>
/// Grimoire's identity and capability budget: the rich-TTY profile, every expressive feature on and
/// no length cap, so Morgana's adapter has nothing to degrade and the terminal receives the reply whole.
/// </summary>
/// <remarks>
/// Identity stays channel-side rather than in the shared terminal library, which hosts the behaviour
/// the two TTY channels have in common and takes the profile as its only input.
/// </remarks>
public static class GrimoireChannelProfile
{
    /// <summary>The profile announced at the handshake and signed into every token.</summary>
    public static readonly ChannelProfile Instance = new(
        ChannelName: "grimoire",
        DisplayName: "Grimoire",
        Capabilities: new ChannelCapabilities(
            SupportsRichCards: true,
            SupportsQuickReplies: true,
            SupportsStreaming: true,
            SupportsMarkdown: true,
            MaxMessageLength: null),

        Introduction: "Talk with Morgana from your terminal: write what you need and press Enter. She answers herself "
                      + "or hands you to the agent who knows the subject. Replies stream in as they are written.",

        // Options a reply offers are Grimoire's alone, so picking one is the key a newcomer meets first
        KeyBindings:
        [
            ("Enter", "send"),
            ("↑↓ Enter", "pick an option"),
            ("↑↓ PgUp PgDn", "scroll back"),
            ("←→", "move the caret"),
            ("Esc", "leave")
        ]);
}
