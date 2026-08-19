# Widget

An embeddable launcher that puts a Cauldron instance into any existing web page.

Closed, it is a floating pill in the bottom-right corner: Morgana's animated face and the
words *Consult Morgana*. Opened, it reveals a panel holding an `<iframe>` pointed at the
Cauldron instance that served the widget. That is the entire feature.

## What it is made of

Three static files. No framework, no build step, no npm, no server code:

| File | Role |
|---|---|
| `wwwroot/morgana-widget.js` | Discovers the Cauldron origin, mounts the launcher, opens/closes the panel |
| `wwwroot/morgana-widget.css` | The launcher and panel styling, in Cauldron's palette |
| `wwwroot/morgana-animated.gif` | Morgana's face on the button |
| `wwwroot/morgana.html` | A sample host page, with styling of its own |

The `.csproj` is a static-asset Razor Class Library: it compiles no code and exists so the
files travel with `Cauldron` and get published under `/widget/`. `Cauldron.csproj` takes the
`ProjectReference` — the widget must be *served by* the instance it embeds, so the dependency
points that way and no separate host or CDN is involved.

## Integrating it into a host site

```html
<script src="https://your-cauldron-host/widget/morgana-widget.js" defer></script>
```

That is the whole contract, and it is plain HTML — the host page's server technology never
enters into it. JSP, PHP, ASP, Rails, WordPress, a hand-written `.html`: if it can emit a
`<script>` tag, it can host the widget.

There are no parameters. The widget reads its own `src` to learn which Cauldron to open, so
the tag copied from one deployment automatically points at that deployment.

## Operator setup

One configuration key, on the **Cauldron** side (`Cauldron:Widget:AllowedEmbedOrigins`):

```json
"Cauldron": {
  "Widget": {
    "AllowedEmbedOrigins": [ "https://www.example.com", "https://shop.example.com" ]
  }
}
```

Each entry is a site permitted to frame Cauldron, emitted as a CSP `frame-ancestors`
directive. **The list is closed by default**: unconfigured, only Cauldron's own pages may
frame it, so a fresh checkout can run `/widget/morgana.html` but no external site can embed
the widget until its origin is listed. Adding origins is therefore a deliberate act, and
removing one revokes that site's ability to host the chat.

Note this is the browser's enforcement of *who may frame Cauldron*, not authentication.
Cauldron continues to authenticate to Morgana with its own issuer key exactly as before —
the widget introduces no new channel and no new issuer, it is a second way to reach the
same Cauldron.

## Trying it

```bash
cd Channels/Cauldron
dotnet run
```

Then open <https://localhost:5002/widget/morgana.html> — a mock third-party page carrying only
the script tag. The launcher appears bottom-right; clicking it loads Cauldron in the panel.

To exercise the genuinely cross-origin path, serve `morgana.html` from a different origin (for
instance `python3 -m http.server 8080` in a copy of that directory, pointing the script tag
at `https://localhost:5002/widget/morgana-widget.js`) and add `http://localhost:8080` to
`AllowedEmbedOrigins`.

## Design notes

**Zero JavaScript dependencies, deliberately.** A widget is injected into a page whose stack
it cannot know; every library it carries is a version conflict waiting to happen and weight
charged to someone else's page load. What a launcher genuinely needs, browsers now provide
natively — which is why the professional widgets in this category (Intercom, Crisp, Drift)
ship the same way.

**Shadow DOM for style isolation.** The launcher lives in a closed shadow root, so the host
page's resets, utility classes and `!important` rules cannot deform it, and nothing here
leaks onto the host's own elements. Inheritance still crosses the boundary, so the stylesheet
restates every inherited property it depends on rather than trusting the host's typography.

**Iframe for execution isolation.** The conversation runs in Cauldron's own origin. The host
page cannot read the transcript and the transcript cannot touch the host page — a guarantee
no same-page embedding could offer. `sandbox` and `referrerpolicy` are set explicitly;
`allow-same-origin` grants the frame *Cauldron's* origin, which its `localStorage`-based
conversation persistence needs, not the host page's.

**The iframe is created on first open and never destroyed.** Cauldron is Blazor Server: loading
it opens a circuit and pins per-visitor state on the server, so mounting eagerly would charge
every page view for a conversation nobody asked for. Closing hides the panel instead of
unmounting, because that circuit *is* the conversation — tearing it down would drop the
SignalR connection and the exchange in flight.

## Known constraints

- **Third-party storage partitioning.** Cauldron persists the conversation id in
  `localStorage`. In a third-party iframe, browsers partition that storage per embedding
  site — which is the behaviour you want (each host site gets its own conversation), but it
  means a visitor's conversation does not follow them from the host site to Cauldron's own
  page. Where a browser blocks third-party storage outright, resume degrades to starting a
  fresh conversation each visit rather than failing.
- **Escape closes only while focus is on the host page.** Keystrokes inside the iframe belong
  to Cauldron's cross-origin document and are not observable from the widget. The launcher,
  which is outside the iframe, always closes the panel.
- **`morgana-animated.gif` is ~1.1 MB.** It is loaded on every page that carries the widget.
  Re-encoding it as animated WebP, or serving a static avatar until first hover, would repay
  itself on a high-traffic host site.
