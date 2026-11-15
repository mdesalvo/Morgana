(function () {
  // The brew behind the page: a few wisps of smoke and a scatter of glowing motes, each with its own place, size,
  // pace and drift so nothing moves in step. Fewer on a phone, where the brew sits behind the text.
  var brew = document.querySelector('.brew');
  if (brew && !matchMedia('(prefers-reduced-motion: reduce)').matches) {
    var phone = window.innerWidth < 640;
    var light = matchMedia('(prefers-color-scheme: light)').matches;
    function scatter(kind, count, make) {
      for (var n = 0; n < count; n++) {
        var el = document.createElement('i');
        el.className = kind + (Math.random() < .35 ? ' pink' : '');
        el.style.left = (Math.random() * 100) + '%';
        make(el);
        brew.appendChild(el);
      }
    }
    scatter('smoke', phone ? 3 : 6, function (el) {
      var size = 28 + Math.random() * 22;
      el.style.width = size + 'vmax';
      el.style.height = (size * 1.3) + 'vmax';
      el.style.marginLeft = (-size / 2) + 'vmax';
      el.style.animationDuration = (40 + Math.random() * 30) + 's';
      el.style.animationDelay = (-Math.random() * 60) + 's';
      el.style.setProperty('--drift', ((Math.random() - .5) * 24) + 'vw');
      el.style.setProperty('--peak', String((light ? .18 : .32) + Math.random() * .12));
    });
    scatter('mote', phone ? 8 : 18, function (el) {
      var size = 2 + Math.random() * 3;
      el.style.width = el.style.height = size + 'px';
      el.style.animationDuration = (18 + Math.random() * 20) + 's, ' + (2 + Math.random() * 3) + 's';
      el.style.animationDelay = (-Math.random() * 30) + 's, ' + (-Math.random() * 4) + 's';
      el.style.setProperty('--drift', ((Math.random() - .5) * 120) + 'px');
    });
  }

  // The menu: a drawer below 1000px, closed by the shade, by Escape or by choosing a page.
  var menu = document.querySelector('.menu');
  var side = document.getElementById('side');
  var shade = document.querySelector('.shade');
  function setOpen(open) {
    side.classList.toggle('open', open);
    shade.hidden = !open;
    menu.setAttribute('aria-expanded', open ? 'true' : 'false');
    document.body.style.overflow = open ? 'hidden' : '';
  }
  menu.addEventListener('click', function () { setOpen(!side.classList.contains('open')); });
  shade.addEventListener('click', function () { setOpen(false); });
  side.querySelectorAll('nav a').forEach(function (a) { a.addEventListener('click', function () { setOpen(false); }); });

  // The current page stays in view inside a long menu.
  var here = side.querySelector('a.here');
  if (here) { here.scrollIntoView({ block: 'center' }); window.scrollTo(0, 0); }

  // Every table scrolls inside its own box, so a phone never scrolls the page sideways.
  document.querySelectorAll('.prose table').forEach(function (t) {
    if (t.parentElement.classList.contains('table')) { return; }
    var box = document.createElement('div');
    box.className = 'table';
    t.parentNode.insertBefore(box, t);
    box.appendChild(t);
  });

  // Screenshots open full size.
  var light = document.querySelector('.lightbox');
  document.querySelectorAll('.shot img').forEach(function (img) {
    img.addEventListener('click', function () { light.querySelector('img').src = img.src; light.hidden = false; });
  });
  light.addEventListener('click', function () { light.hidden = true; });

  // A copy button on every code block.
  document.querySelectorAll('.prose pre').forEach(function (pre) {
    var b = document.createElement('button');
    b.className = 'copy';
    b.type = 'button';
    b.textContent = 'Copy';
    b.addEventListener('click', function () {
      var code = pre.querySelector('code') || pre;
      navigator.clipboard.writeText(code.innerText.trim()).then(function () {
        b.textContent = 'Copied';
        setTimeout(function () { b.textContent = 'Copy'; }, 1500);
      });
    });
    pre.appendChild(b);
  });

  // Search over search.json: a title match ranks above a body match and each hit shows where it matched.
  var input = document.getElementById('q');
  var hits = document.getElementById('hits');
  var index = null;
  function load(done) {
    if (index) { return done(); }
    fetch(window.MORGANA_SEARCH).then(function (r) { return r.json(); })
      .then(function (data) { index = data; done(); })
      .catch(function () { index = []; done(); });
  }
  function esc(s) { return s.replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
  function run() {
    var terms = input.value.toLowerCase().split(/\s+/).filter(function (t) { return t.length > 1; });
    if (!terms.length) { hits.hidden = true; return; }
    var found = [];
    index.forEach(function (page) {
      var title = page.title.toLowerCase(), text = page.text.toLowerCase(), score = 0, first = -1;
      for (var i = 0; i < terms.length; i++) {
        var inTitle = title.indexOf(terms[i]) >= 0, at = text.indexOf(terms[i]);
        if (!inTitle && at < 0) { return; }
        score += (inTitle ? 10 : 0) + (at >= 0 ? 1 : 0);
        if (first < 0 && at >= 0) { first = at; }
      }
      var from = Math.max(0, first - 50);
      found.push({ page: page, score: score, excerpt: first >= 0 ? (from > 0 ? '…' : '') + page.text.substr(from, 130) + '…' : '' });
    });
    found.sort(function (a, b) { return b.score - a.score; });
    hits.innerHTML = found.length
      ? found.slice(0, 8).map(function (h) { return '<a href="' + h.page.url + '"><strong>' + esc(h.page.title) + '</strong><span>' + esc(h.excerpt) + '</span></a>'; }).join('')
      : '<div class="none">Nothing found for these words.</div>';
    hits.hidden = false;
  }
  input.addEventListener('input', function () { load(run); });
  document.addEventListener('click', function (e) { if (!e.target.closest('.search')) { hits.hidden = true; } });
  document.addEventListener('keydown', function (e) {
    var typing = /input|textarea/i.test((document.activeElement || {}).tagName || '');
    if (e.key === '/' && !typing) {
      e.preventDefault();
      if (window.innerWidth < 1000) { setOpen(true); }
      input.focus();
    }
    if (e.key === 'Escape') { hits.hidden = true; light.hidden = true; setOpen(false); }
  });
})();
