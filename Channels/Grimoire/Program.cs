using Morgana.Terminal;
using Morgana.Terminal.Extensions;
using Grimoire.Messages;
using Grimoire.Services;
using Morgana.Terminal.Interfaces;

// ==============================================================================
// GRIMOIRE - WEBHOOK CHANNEL FOR MORGANA
// ==============================================================================
// Grimoire is the rich-TTY reference channel for Morgana: a "textual Cauldron" that consumes Morgana's full
// expressive surface (rich cards, quick replies, streaming, markdown, no message-length cap) inside a
// Spectre.Console terminal UI. Where Cauldron renders the full profile in HTML, Grimoire renders it as ANSI
// primitives: content arrives integral, never degraded; it is Spectrized locally rather than rewritten by
// MorganaChannelAdapter.
//
// Architecture: a Kestrel-hosted HTTPS listener on port 5004 receives inbound ChannelMessage payloads at
// POST /morgana-hook (and incremental stream chunks at POST /morgana-hook/chunk) and drives a Spectre.Console
// live display; user input is captured from stdin and sent back to Morgana via the REST conversation endpoints,
// authenticated with a self-issued JWT signed under iss=grimoire (the matching entry must live in Morgana's
// Authentication:Issuers). Everything the two TTY channels do the same way lives in Morgana.Terminal; what is
// Grimoire's own is its profile, its theme and the markdown and rich card rendering.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
ChannelProfile profile = GrimoireChannelProfile.Instance;

// The process and the configuration are checked before anything else is built
if (!builder.CanStartTerminalChannel(profile))
    return;

// The markdown renderer and the two it composes are registered first, so the shared registration keeps them
// in place of the plain text renderer
builder.Services.AddSingleton<MarkdownTerminalRenderService>();
builder.Services.AddSingleton<RichCardTerminalRenderService>();
builder.Services.AddSingleton<IMessageRenderer, MarkdownMessageRenderService>();

// The shared registration wires the services and keeps the markdown renderer registered above
builder.AddTerminalChannel(profile, GrimoireTerminalTheme.Instance);
WebApplication app = builder.Build();
app.MapMorganaHooks();
await app.RunTerminalChannelAsync();
