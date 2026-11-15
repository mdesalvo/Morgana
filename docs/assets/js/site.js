(function () {
  // Mobile navigation drawer.
  var btn = document.querySelector('.menu-btn');
  var side = document.getElementById('sidebar');
  if (btn && side) {
    btn.addEventListener('click', function () {
      var open = side.classList.toggle('open');
      btn.setAttribute('aria-expanded', open ? 'true' : 'false');
    });
  }

  // Keep the active page visible in a long sidebar.
  var active = document.querySelector('.nav-link.active');
  if (active && active.scrollIntoView) { active.scrollIntoView({ block: 'center' }); }

  // "On this page": built from the article's h2 and h3.
  var toc = document.getElementById('toc');
  var heads = document.querySelectorAll('.prose h2, .prose h3');
  if (toc && heads.length > 2) {
    var html = '<div class="toc-title">On this page</div>';
    heads.forEach(function (h) {
      if (!h.id) {
        h.id = h.textContent.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
      }
      html += '<a class="' + (h.tagName === 'H3' ? 'sub' : '') + '" href="#' + h.id + '">' + h.textContent + '</a>';
    });
    toc.innerHTML = html;
    var links = toc.querySelectorAll('a');
    var obs = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting) {
          links.forEach(function (l) { l.classList.toggle('on', l.getAttribute('href') === '#' + e.target.id); });
        }
      });
    }, { rootMargin: '-10% 0px -80% 0px' });
    heads.forEach(function (h) { obs.observe(h); });
  } else if (toc) { toc.style.display = 'none'; }

  // Screenshots open full size on click.
  var box = document.getElementById('lightbox');
  document.querySelectorAll('.shot img').forEach(function (img) {
    img.addEventListener('click', function () {
      box.querySelector('img').src = img.src;
      box.hidden = false;
    });
  });
  if (box) { box.addEventListener('click', function () { box.hidden = true; }); }
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && box) { box.hidden = true; } });

  // Copy button on code blocks.
  document.querySelectorAll('.prose pre').forEach(function (pre) {
    if (pre.classList.contains('mermaid')) { return; }
    var b = document.createElement('button');
    b.className = 'copy'; b.textContent = 'Copy';
    b.addEventListener('click', function () {
      navigator.clipboard.writeText(pre.innerText.replace(/Copy$/, '').trim()).then(function () {
        b.textContent = 'Copied'; setTimeout(function () { b.textContent = 'Copy'; }, 1500);
      });
    });
    pre.appendChild(b);
  });
})();

// Instant search over search.json: title matches first and then body matches, with the best excerpt.
(function () {
  var input = document.getElementById('search-input');
  var box = document.getElementById('search-results');
  if (!input || !box || !window.MORGANA_SEARCH) { return; }
  var index = null;
  function load(done) {
    if (index) { return done(); }
    fetch(window.MORGANA_SEARCH).then(function (r) { return r.json(); }).then(function (data) { index = data; done(); }).catch(function () { index = []; done(); });
  }
  function esc(s) { return s.replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
  function run() {
    var terms = input.value.toLowerCase().split(/\s+/).filter(function (t) { return t.length > 1; });
    if (!terms.length) { box.hidden = true; return; }
    var hits = [];
    index.forEach(function (page) {
      var title = page.title.toLowerCase(), text = page.text.toLowerCase(), score = 0, first = -1;
      for (var i = 0; i < terms.length; i++) {
        var inTitle = title.indexOf(terms[i]) >= 0, at = text.indexOf(terms[i]);
        if (!inTitle && at < 0) { return; }
        score += (inTitle ? 10 : 0) + (at >= 0 ? 1 + Math.min(5, text.split(terms[i]).length - 1) * .2 : 0);
        if (first < 0 && at >= 0) { first = at; }
      }
      var from = Math.max(0, first - 40);
      hits.push({ page: page, score: score, excerpt: first >= 0 ? (from > 0 ? '…' : '') + page.text.substr(from, 120) + '…' : '' });
    });
    hits.sort(function (a, b) { return b.score - a.score; });
    box.innerHTML = hits.length ? hits.slice(0, 7).map(function (h) {
      return '<a href="' + h.page.url + '"><strong>' + esc(h.page.title) + '</strong><span>' + esc(h.excerpt) + '</span></a>';
    }).join('') : '<div class="none">Nothing found. Try <a href="' + window.MORGANA_SEARCH.replace('search.json', 'help/') + '">Stuck? Start here</a>.</div>';
    box.hidden = false;
  }
  input.addEventListener('input', function () { load(run); });
  input.addEventListener('focus', function () { load(run); });
  document.addEventListener('keydown', function (e) {
    var typing = /input|textarea/i.test((document.activeElement || {}).tagName || '');
    if (e.key === '/' && !typing) { e.preventDefault(); input.focus(); }
    if (e.key === 'Escape') { box.hidden = true; input.blur(); }
  });
  document.addEventListener('click', function (e) { if (!document.getElementById('search').contains(e.target)) { box.hidden = true; } });
})();
