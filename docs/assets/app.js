/* ai-runner docs - one shared script. No framework. */
(function () {
  'use strict';

  var root = document.documentElement;
  var HLJS_BASE = 'https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/styles/';

  function store(key, value) {
    try {
      if (value === undefined) return localStorage.getItem(key);
      localStorage.setItem(key, value);
    } catch (e) { /* storage can be blocked (file:// in some browsers) */ }
    return null;
  }

  function currentTheme() {
    return root.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';
  }

  // ---------- theme ----------
  function applyTheme(theme, save) {
    root.setAttribute('data-theme', theme);
    if (save) store('airunner-docs-theme', theme);
    var link = document.getElementById('hljs-theme');
    if (link) link.href = HLJS_BASE + (theme === 'dark' ? 'github-dark.min.css' : 'github.min.css');
    var btn = document.getElementById('theme-toggle');
    if (btn) btn.textContent = theme === 'dark' ? 'Light mode' : 'Dark mode';
  }

  function initTheme() {
    applyTheme(currentTheme(), false); // the inline script in <head> already set data-theme
    var btn = document.getElementById('theme-toggle');
    if (!btn) return;
    btn.addEventListener('click', function () {
      applyTheme(currentTheme() === 'dark' ? 'light' : 'dark', true);
      if (window.renderDiagrams) window.renderDiagrams(); // Mermaid needs a new theme
    });
    // Follow the OS while the user has not chosen.
    if (window.matchMedia) {
      window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function (e) {
        if (store('airunner-docs-theme')) return;
        applyTheme(e.matches ? 'dark' : 'light', false);
        if (window.renderDiagrams) window.renderDiagrams();
      });
    }
  }

  // ---------- sidebar: highlight the current page ----------
  function initNav() {
    var here = location.pathname.replace(/\/index\.html$/, '/');
    var links = document.querySelectorAll('.nav a');
    var found = false;
    links.forEach(function (a) {
      var target = new URL(a.getAttribute('href'), location.href).pathname.replace(/\/index\.html$/, '/');
      if (target === here) { a.classList.add('current'); a.setAttribute('aria-current', 'page'); found = true; }
    });
    if (!found) { /* keep the statically rendered "current" class */ }

    var menu = document.getElementById('menu-btn');
    if (menu) menu.addEventListener('click', function () { document.body.classList.toggle('nav-open'); });
    document.querySelector('.main').addEventListener('click', function () { document.body.classList.remove('nav-open'); });
  }

  // ---------- code: highlight + copy ----------
  function initCode() {
    if (window.hljs) {
      document.querySelectorAll('pre code').forEach(function (el) { window.hljs.highlightElement(el); });
    }
    document.querySelectorAll('pre').forEach(function (pre) {
      if (pre.classList.contains('mermaid') || !pre.querySelector('code')) return;
      var btn = document.createElement('button');
      btn.type = 'button'; btn.className = 'copy-btn'; btn.textContent = 'Copy';
      btn.addEventListener('click', function () {
        var text = pre.querySelector('code').innerText;
        var done = function () { btn.textContent = 'Copied'; setTimeout(function () { btn.textContent = 'Copy'; }, 1200); };
        if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(text).then(done, function () {});
        else done();
      });
      pre.appendChild(btn);
    });
  }

  // ---------- diagrams: click to zoom ----------
  function initZoom() {
    var overlay = document.createElement('div');
    overlay.id = 'zoom-overlay';
    overlay.innerHTML = '<div class="zoom-hint">Click or press Esc to close</div><div class="zoom-inner"></div>';
    document.body.appendChild(overlay);
    var inner = overlay.querySelector('.zoom-inner');

    function close() { overlay.classList.remove('open'); inner.innerHTML = ''; }
    overlay.addEventListener('click', close);
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') close(); });

    document.addEventListener('click', function (e) {
      var box = e.target.closest && e.target.closest('figure .diagram-box');
      if (!box) return;
      var svg = box.querySelector('svg');
      if (!svg) return;
      inner.innerHTML = '';
      var copy = svg.cloneNode(true);
      copy.removeAttribute('style');
      copy.removeAttribute('height');
      copy.removeAttribute('width');
      inner.appendChild(copy);
      overlay.classList.add('open');
    });
  }

  function init() {
    initTheme();
    initNav();
    initCode();
    initZoom();
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
