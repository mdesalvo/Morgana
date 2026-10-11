using Morgana.Terminal;
using Morgana.Terminal.Extensions;
using Rune.Messages;

// ==============================================================================
// RUNE - WEBHOOK CHANNEL FOR MORGANA
// ==============================================================================
// Rune is a minimal CLI reference channel talking to Morgana over the webhook delivery mode. It declares a
// tight capability budget (500-char hard limit, no rich cards, no quick replies, no streaming, no markdown)
// so Morgana's channel adapter rewrite path is exercised on every turn.
//
// Architecture: a Kestrel-hosted HTTPS listener on port 5003 receives inbound ChannelMessage payloads at
// POST /morgana-hook and drives a Spectre.Console terminal UI; user input is captured from stdin and sent back
// to Morgana via the REST conversation endpoints, authenticated with a self-issued JWT signed under iss=rune
// (the matching entry must live in Morgana's Authentication:Issuers). Everything the two TTY channels do the
// same way lives in Morgana.Terminal; what is Rune's own is its profile and its theme.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
ChannelProfile profile = RuneChannelProfile.Build(builder.Configuration);

// The process and the configuration are checked before anything else is built
if (!builder.CanStartTerminalChannel(profile))
    return;

builder.AddTerminalChannel(profile, RuneTerminalTheme.Instance);
WebApplication app = builder.Build();
app.MapMorganaHooks();
await app.RunTerminalChannelAsync();
