/*
    ============================================================================
    MORGANA WIDGET — embeddable launcher for a Cauldron instance
    ============================================================================

    The entire integration contract for a host site is one tag:

        <script src="https://your-cauldron-host/widget/morgana-widget.js" defer></script>

    Nothing else. No parameters, no configuration, no host-side markup, no build step —
    which is what makes it droppable into a JSP, a PHP template, a static export or a
    CMS block alike: the host page only has to be able to emit a <script> tag.

    Dependency-free on purpose. A widget lands in a page whose stack it cannot know, so
    every library it brings is a version it might fight with. The three things a chat
    launcher actually needs are browser primitives, not packages: Shadow DOM for style
    isolation, an <iframe> for execution isolation and CSS transitions for motion.
*/

(function () {
    'use strict';

    // A second copy of the tag (a CMS block included twice, a partial rendered per-region)
    // must not mean a second launcher. First one in wins and the rest return silently.
    if (window.__morganaWidgetLoaded)
        return;
    window.__morganaWidgetLoaded = true;

    // ========================================================================
    // ORIGIN DISCOVERY
    // ========================================================================
    // Where Cauldron lives is not configured, it is observed: the widget is served BY the
    // Cauldron instance it belongs to, so the script's own URL already names the host. This
    // is what buys the zero-parameter contract — copying the tag from a different Cauldron
    // deployment automatically points the iframe at that deployment.

    var ownScript = document.currentScript || (function () {
        // currentScript is null when the tag is loaded from a module or re-inserted by a
        // script loader; fall back to the last matching tag in the document.
        var candidates = document.querySelectorAll('script[src*="morgana-widget.js"]');
        return candidates[candidates.length - 1];
    })();

    if (!ownScript || !ownScript.src) {
        // Without a resolvable src there is no way to know which Cauldron to talk to and a
        // launcher pointing nowhere is worse than no launcher.
        console.error('[Morgana] widget script has no resolvable src; not mounting.');
        return;
    }

    var scriptUrl = new URL(ownScript.src, document.baseURI);
    var assetsBase = new URL('.', scriptUrl).href;  // .../widget/
    var cauldronUrl = new URL('/', scriptUrl).href; // Cauldron's chat page, at the host root

    // ========================================================================
    // MOUNT POINT
    // ========================================================================
    // A closed shadow root, so the widget's CSS and the host page's CSS cannot reach each
    // other in either direction: no reset/utility framework on the page can restyle the
    // launcher and nothing here leaks out onto the host's own elements. Closed rather than
    // open so host-page scripts cannot walk into the widget's internals either.

    var host = document.createElement('div');
    host.id = 'morgana-widget';
    // The only style applied outside the shadow root, because it governs the host element
    // itself: the widget floats above the page and never participates in its layout.
    host.style.cssText = 'position:fixed;inset:auto 0 0 auto;z-index:2147483000;';

    var shadow = host.attachShadow({ mode: 'closed' });

    // Stylesheet stays a real .css file, linked inside the shadow root rather than inlined
    // as a JS string: it remains editable, cacheable and reviewable as CSS.
    var styles = document.createElement('link');
    styles.rel = 'stylesheet';
    styles.href = assetsBase + 'morgana-widget.css';
    shadow.appendChild(styles);

    // ========================================================================
    // LAUNCHER (closed state)
    // ========================================================================

    var launcher = document.createElement('button');
    launcher.className = 'launcher';
    launcher.type = 'button';
    launcher.setAttribute('aria-expanded', 'false');
    launcher.setAttribute('aria-label', 'Consult Morgana');
    // The dismiss glyph is a chevron rather than a cross and the wording follows it: closing
    // hides the panel while the conversation keeps running behind it, so a cross — which
    // everywhere else means "discard this" — would promise a teardown that does not happen.
    launcher.innerHTML =
        '<span class="launcher-avatar">' +
            '<span class="glow" aria-hidden="true"></span>' +
            '<img src="' + assetsBase + 'morgana-animated.webp" alt="" aria-hidden="true" />' +
        '</span>' +
        '<span class="launcher-label">Consult Morgana</span>' +
        '<svg class="launcher-minimize" viewBox="0 0 24 24" aria-hidden="true" focusable="false">' +
            '<path d="M5.5 9l6.5 6.5 6.5-6.5" fill="none" stroke="currentColor" stroke-width="2.6" ' +
                  'stroke-linecap="round" stroke-linejoin="round" />' +
        '</svg>';

    // ========================================================================
    // PANEL (open state)
    // ========================================================================

    var panel = document.createElement('div');
    panel.className = 'panel';
    panel.setAttribute('role', 'dialog');
    panel.setAttribute('aria-label', 'Chat with Morgana');
    panel.setAttribute('aria-modal', 'false');
    // Hidden from assistive tech and from tab order until it is actually opened.
    panel.setAttribute('hidden', '');

    shadow.appendChild(panel);
    shadow.appendChild(launcher);

    document.body.appendChild(host);

    // ========================================================================
    // IFRAME LIFECYCLE
    // ========================================================================
    // Created on first open, never destroyed afterwards.
    //
    // Deferred because Cauldron is a Blazor Server app: loading the page opens a circuit and
    // pins per-visitor state on the server. Mounting the iframe eagerly would charge every
    // page view of the host site for a conversation nobody asked for.
    //
    // Kept alive on close because that same circuit *is* the conversation: tearing the iframe
    // down on every close would drop the SignalR connection and the in-flight exchange with it,
    // so closing hides the panel instead.

    var frame = null;

    function mountFrame() {
        if (frame)
            return;

        frame = document.createElement('iframe');
        frame.className = 'frame';
        frame.title = 'Morgana';
        frame.src = cauldronUrl;

        // allow-same-origin reads as a hole but is not one here: it grants the frame its own
        // origin (Cauldron's), not the host page's and Cauldron needs it for the localStorage
        // its conversation persistence is built on. Everything the frame does stays walled off
        // from the embedding document either way.
        frame.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-forms allow-popups allow-popups-to-escape-sandbox');

        // The host page's full URL is none of the backend's business; the origin is enough
        // to tell deployments apart in logs.
        frame.setAttribute('referrerpolicy', 'strict-origin-when-cross-origin');

        panel.appendChild(frame);
    }

    // ========================================================================
    // OPEN / CLOSE
    // ========================================================================

    var isOpen = false;

    function open() {
        if (isOpen)
            return;
        isOpen = true;

        mountFrame();
        panel.removeAttribute('hidden');

        // Set on the next frame so the element is laid out before the transition class lands,
        // otherwise the browser has nothing to animate from and the panel simply appears.
        requestAnimationFrame(function () {
            panel.classList.add('open');
        });

        launcher.classList.add('is-open');
        launcher.setAttribute('aria-expanded', 'true');
        launcher.setAttribute('aria-label', 'Minimize Morgana');

        // Moves the caret straight into the conversation, so a visitor who opened with the
        // keyboard can start typing without hunting for the input.
        frame.focus();
    }

    function close() {
        if (!isOpen)
            return;
        isOpen = false;

        panel.classList.remove('open');

        launcher.classList.remove('is-open');
        launcher.setAttribute('aria-expanded', 'false');
        launcher.setAttribute('aria-label', 'Consult Morgana');
        launcher.focus();

        // hidden goes back on only once the closing transition has played, so the panel is
        // out of the tab order and out of the accessibility tree while invisible.
        var onDone = function (event) {
            if (event.target !== panel)
                return;
            panel.removeEventListener('transitionend', onDone);
            if (!isOpen)
                panel.setAttribute('hidden', '');
        };
        panel.addEventListener('transitionend', onDone);
    }

    launcher.addEventListener('click', function () {
        if (isOpen)
            close();
        else
            open();
    });

    // Escape closes, as any overlay should. Note this only fires while focus is on the host
    // page: keystrokes inside the iframe belong to Cauldron's document and are not observable
    // from here, which is the cross-origin isolation working as intended.
    document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape' && isOpen)
            close();
    });
})();
