namespace Morgana.Terminal.Messages;

/// <summary>The size of the terminal a frame is drawn against.</summary>
/// <param name="Width">Columns available, at least one.</param>
/// <param name="Height">Rows available.</param>
public readonly record struct TerminalViewport(int Width, int Height);
