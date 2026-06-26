/* KillerPDF site - shared chrome behavior (theme, accent, language, easter egg).
   Page-specific behavior (sidebar thumbnails, accordions) stays inline per page. */
(function () {
  var root = document.documentElement;
  var THEMES = ['dark','light','hc','blood','greed','cyanotic'];
  var NEUTRAL = ['dark','light','hc'];
  // [ bright accent (highlights/text/outline), darker fill (solid button) ]
  var ACCENTS = { green:['#1ea54c','#1ea54c'], red:['#DD504B','#c23b36'], orange:['#E8962C','#c97c14'], teal:['#1FB8A8','#178f83'], blue:['#50AEE8','#2f86c2'], purple:['#B982E3','#9a5cc9'] };

  var swatches = [].slice.call(document.querySelectorAll('.swatch'));
  var accDots  = [].slice.call(document.querySelectorAll('.acc'));
  var accentSwitch = document.getElementById('accentSwitch');
  var curAccent = 'green';

  function applyAccent(name) {
    if (!ACCENTS[name]) name = 'green';
    curAccent = name;
    var neutral = NEUTRAL.indexOf(root.getAttribute('data-theme')) >= 0;
    if (neutral) {
      root.style.setProperty('--accent', ACCENTS[name][0]);
      root.style.setProperty('--btn', ACCENTS[name][1]);
    } else {
      root.style.removeProperty('--accent');
      root.style.removeProperty('--btn');
    }
    accDots.forEach(function (d) { d.setAttribute('aria-pressed', d.dataset.accent === name ? 'true' : 'false'); });
    try { localStorage.setItem('kpdf-accent', name); } catch (e) {}
  }

  function setTheme(name) {
    if (THEMES.indexOf(name) < 0) name = 'dark';
    root.setAttribute('data-theme', name);
    try { localStorage.setItem('kpdf-theme', name); } catch (e) {}
    swatches.forEach(function (s) { s.setAttribute('aria-pressed', s.dataset.theme === name ? 'true' : 'false'); });
    if (accentSwitch) accentSwitch.hidden = NEUTRAL.indexOf(name) < 0;
    applyAccent(curAccent);
  }

  swatches.forEach(function (s) { s.addEventListener('click', function () { setTheme(s.dataset.theme); }); });
  accDots.forEach(function (d) { d.addEventListener('click', function () { applyAccent(d.dataset.accent); }); });

  // ---- i18n (English complete; other languages fall back to English until translated) ----
  var I18N = {};
  var EN = {};
  document.querySelectorAll('[data-i18n]').forEach(function (n) { EN[n.getAttribute('data-i18n')] = n.innerHTML; });
  var LANGS = ['en','es','de','fr','tr','zh','zh-cn','bn'];
  var FLAGS = {
    en: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fff"/><g fill="#b22234"><rect width="24" height="1.85"/><rect y="3.7" width="24" height="1.85"/><rect y="7.4" width="24" height="1.85"/><rect y="11.1" width="24" height="1.85"/><rect y="14.8" width="24" height="1.85"/><rect y="18.5" width="24" height="1.85"/><rect y="22.2" width="24" height="1.8"/></g><rect width="11" height="12.95" fill="#3c3b6e"/></svg>',
    es: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#c60b1e"/><rect y="6" width="24" height="12" fill="#ffc400"/></svg>',
    de: '<svg viewBox="0 0 24 24"><rect width="24" height="8" fill="#000"/><rect y="8" width="24" height="8" fill="#dd0000"/><rect y="16" width="24" height="8" fill="#ffce00"/></svg>',
    fr: '<svg viewBox="0 0 24 24"><rect width="8" height="24" fill="#0055a4"/><rect x="8" width="8" height="24" fill="#fff"/><rect x="16" width="8" height="24" fill="#ef4135"/></svg>',
    tr: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#e30a17"/><circle cx="9.5" cy="12" r="5" fill="#fff"/><circle cx="11" cy="12" r="4" fill="#e30a17"/><polygon points="15.5,9.4 16.12,11.15 17.97,11.2 16.5,12.32 17.03,14.1 15.5,13.05 13.97,14.1 14.5,12.32 13.03,11.2 14.88,11.15" fill="#fff"/></svg>',
    zh: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fe0000"/><rect width="12" height="12" fill="#000095"/><polygon points="6,3 7.2,6.6 11,6.6 7.9,8.8 9.1,12.4 6,10.2 2.9,12.4 4.1,8.8 1,6.6 4.8,6.6" fill="#fff"/></svg>',
    'zh-cn': '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#de2910"/><polygon points="4,3 4.9,5.6 7.6,5.6 5.4,7.3 6.2,9.9 4,8.3 1.8,9.9 2.6,7.3 0.4,5.6 3.1,5.6" fill="#ffde00"/></svg>',
    bn: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#006a4e"/><circle cx="10.5" cy="12" r="6" fill="#f42a41"/></svg>'
  };
  var langItems = [].slice.call(document.querySelectorAll('.lang-item'));
  var langToggle = document.getElementById('langToggle');
  var langMenu = document.getElementById('langMenu');

  function applyLang(lang) {
    if (LANGS.indexOf(lang) < 0) lang = 'en';
    root.setAttribute('lang', lang === 'zh' ? 'zh-Hant' : (lang === 'zh-cn' ? 'zh-Hans' : lang));
    var dict = (lang === 'en') ? EN : (I18N[lang] || {});
    document.querySelectorAll('[data-i18n]').forEach(function (n) {
      var k = n.getAttribute('data-i18n');
      n.innerHTML = (dict && dict[k] != null) ? dict[k] : EN[k];
    });
    langItems.forEach(function (b) { b.setAttribute('aria-pressed', b.dataset.lang === lang ? 'true' : 'false'); });
    if (langToggle) langToggle.innerHTML = FLAGS[lang] || FLAGS.en;
    try { localStorage.setItem('kpdf-lang', lang); } catch (e) {}
  }
  function closeLangMenu() { if (langMenu) { langMenu.hidden = true; langToggle.setAttribute('aria-expanded', 'false'); } }
  if (langToggle && langMenu) {
    langToggle.addEventListener('click', function (e) {
      e.stopPropagation();
      var willOpen = langMenu.hidden;
      langMenu.hidden = !willOpen;
      langToggle.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
    });
    langItems.forEach(function (b) { b.addEventListener('click', function () { applyLang(b.dataset.lang); closeLangMenu(); }); });
    document.addEventListener('click', function (e) { if (!langMenu.hidden && !e.target.closest('.lang-switch')) closeLangMenu(); });
  }

  // ---- Easter egg: click the version number ----
  var verEgg = document.getElementById('verEgg');
  var eggToast = document.getElementById('eggToast');
  if (verEgg) verEgg.addEventListener('click', function () {
    for (var i = 0; i < 18; i++) {
      var d = document.createElement('span');
      d.className = 'drip';
      d.style.left = (Math.random() * 100) + 'vw';
      d.style.height = (18 + Math.random() * 64) + 'px';
      d.style.opacity = (0.6 + Math.random() * 0.4).toFixed(2);
      var dur = 1.1 + Math.random() * 1.6;
      d.style.animation = 'dripfall ' + dur + 's linear forwards';
      d.style.animationDelay = (Math.random() * 0.5) + 's';
      document.body.appendChild(d);
      (function (el) { setTimeout(function () { el.remove(); }, (dur + 0.8) * 1000); })(d);
    }
    if (eggToast) {
      eggToast.textContent = 'No subscriptions were harmed in the making of this PDF editor.';
      eggToast.classList.add('show');
      clearTimeout(verEgg._t);
      verEgg._t = setTimeout(function () { eggToast.classList.remove('show'); }, 2800);
    }
  });

  // ---- Init ----
  var savedTheme = 'dark', savedAccent = 'green', savedLang = 'en';
  try { savedTheme = localStorage.getItem('kpdf-theme') || 'dark'; } catch (e) {}
  try { savedAccent = localStorage.getItem('kpdf-accent') || 'green'; } catch (e) {}
  try { savedLang = localStorage.getItem('kpdf-lang') || 'en'; } catch (e) {}
  curAccent = savedAccent;
  setTheme(savedTheme);
  applyLang(savedLang);
})();
