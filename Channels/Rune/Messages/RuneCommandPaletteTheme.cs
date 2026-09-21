using Morgana.Terminal.Messages;

namespace Rune.Messages;

/// <summary>
/// Rune's primary on the shared command palette: its emerald. The palette is local chrome, never a capability
/// declared at the handshake, so giving it to Rune leaves the degradation path it exists for untouched.
/// </summary>
public static class RuneCommandPaletteTheme
{
    /// <summary>The theme the palette is drawn in.</summary>
    public static readonly CommandPaletteTheme Instance = new(PrimaryColor: "#10b981");
}
