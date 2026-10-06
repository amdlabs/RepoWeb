/*
 * Exowise · tema claro / oscuro compartido por todos los productos.
 *
 * La preferencia vive en una cookie del dominio .exowise.ai (y en localStorage como respaldo):
 * se elige una vez y la respetan la landing (exowise.ai), Admin y cada producto
 * (app.exowise.ai/<App>/). Las apps que arman el HTML en el servidor (WebForms) pueden leer
 * esa cookie para pintar el tema correcto sin parpadeo.
 *
 * Uso en cada app (copiar este archivo dentro del proyecto, no enlazarlo desde otra app):
 *   <script src="exowise-tema.js"></script>            ← en el <head>, antes del CSS, sin defer
 *   <div data-exowise-tema></div>                        ← donde va el selector (opcional)
 * El CSS de la app define su versión oscura con  :root[data-theme="dark"] { ... }
 *
 * Valores guardados (clave "exowise.tema"): "claro" | "oscuro" | "auto" (sigue al sistema).
 * En <html> quedan: data-theme="light|dark" (el efectivo) y data-tema="claro|oscuro|auto".
 * Evento: window "exowise:tema" con detail { preferido, efectivo } cada vez que cambia.
 *
 * Fuente canónica: EXOWISE.IA\Marca\exowise-tema.js en ExoWiseMgr.
 */
(function () {
  'use strict';
  var CLAVE = 'exowise.tema';
  var VALORES = ['claro', 'oscuro', 'auto'];
  var media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;

  // La cookie manda: con domain=.exowise.ai la comparten exowise.ai (landing) y app.exowise.ai
  // (productos), que son orígenes distintos y no ven el mismo localStorage.
  function leer() {
    var m = document.cookie.match(/(?:^|;\s*)exowise\.tema=(claro|oscuro|auto)/);
    if (m) return m[1];
    var v = null;
    try { v = localStorage.getItem(CLAVE); } catch (e) { /* sin almacenamiento */ }
    return VALORES.indexOf(v) < 0 ? 'auto' : v;
  }

  function dominioCookie() {
    var h = location.hostname;
    return /(^|\.)exowise\.ai$/.test(h) ? '; domain=.exowise.ai' : '';
  }

  function efectivo(pref) {
    if (pref === 'claro') return 'light';
    if (pref === 'oscuro') return 'dark';
    return media && media.matches ? 'dark' : 'light';
  }

  function aplicar(pref) {
    var ef = efectivo(pref);
    var html = document.documentElement;
    html.setAttribute('data-theme', ef);
    html.setAttribute('data-tema', pref);
    html.style.colorScheme = ef;
    marcarSelectores(pref);
    try { window.dispatchEvent(new CustomEvent('exowise:tema', { detail: { preferido: pref, efectivo: ef } })); } catch (e) { /* navegador viejo */ }
  }

  function guardar(pref) {
    if (VALORES.indexOf(pref) < 0) return;
    try { localStorage.setItem(CLAVE, pref); } catch (e) { /* sin almacenamiento */ }
    document.cookie = 'exowise.tema=' + pref + '; path=/; max-age=31536000; SameSite=Lax' + dominioCookie() + (location.protocol === 'https:' ? '; Secure' : '');
    aplicar(pref);
  }

  // ───── Selector (tres botones: claro · oscuro · automático) ─────
  var ICONOS = {
    claro: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><circle cx="12" cy="12" r="4"/><path d="M12 2.5v2M12 19.5v2M4.6 4.6l1.4 1.4M18 18l1.4 1.4M2.5 12h2M19.5 12h2M4.6 19.4 6 18M18 6l1.4-1.4"/></svg>',
    oscuro: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z"/></svg>',
    auto: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/></svg>'
  };
  var TEXTOS = {
    es: { claro: 'Claro', oscuro: 'Oscuro', auto: 'Automático (según el sistema)', grupo: 'Tema' },
    en: { claro: 'Light', oscuro: 'Dark', auto: 'Automatic (follow system)', grupo: 'Theme' },
    pt: { claro: 'Claro', oscuro: 'Escuro', auto: 'Automático (segue o sistema)', grupo: 'Tema' }
  };

  function textos() {
    var l = (document.documentElement.lang || navigator.language || 'es').slice(0, 2).toLowerCase();
    return TEXTOS[l] || TEXTOS.es;
  }

  function estilos() {
    if (document.getElementById('exowise-tema-css')) return;
    var s = document.createElement('style');
    s.id = 'exowise-tema-css';
    s.textContent =
      '.exowise-tema{display:inline-flex;gap:2px;padding:2px;border-radius:999px;border:1px solid var(--exowise-tema-borde,rgba(128,128,128,.35));background:var(--exowise-tema-fondo,transparent)}' +
      '.exowise-tema button{display:grid;place-items:center;width:28px;height:28px;border:0;border-radius:999px;background:none;color:inherit;opacity:.6;cursor:pointer;padding:0}' +
      '.exowise-tema button:hover{opacity:1}' +
      '.exowise-tema button[aria-pressed="true"]{opacity:1;background:var(--exowise-tema-activo,#141414);color:var(--exowise-tema-activo-texto,#f7f1ec)}' +
      '[data-theme="dark"] .exowise-tema button[aria-pressed="true"]{background:var(--exowise-tema-activo,#f7f1ec);color:var(--exowise-tema-activo-texto,#141414)}' +
      '.exowise-tema svg{width:15px;height:15px}' +
      '.exowise-tema button:focus-visible{outline:2px solid #f0a04b;outline-offset:1px}';
    document.head.appendChild(s);
  }

  function montar(el) {
    if (!el || el.getAttribute('data-exowise-tema-listo')) return;
    estilos();
    var t = textos();
    el.classList.add('exowise-tema');
    el.setAttribute('role', 'group');
    el.setAttribute('aria-label', t.grupo);
    el.innerHTML = VALORES.map(function (v) {
      return '<button type="button" data-valor="' + v + '" title="' + t[v] + '" aria-label="' + t[v] + '">' + ICONOS[v] + '</button>';
    }).join('');
    el.addEventListener('click', function (e) {
      var b = e.target.closest('button[data-valor]');
      if (b) guardar(b.getAttribute('data-valor'));
    });
    el.setAttribute('data-exowise-tema-listo', '1');
    marcarSelectores(leer());
  }

  function marcarSelectores(pref) {
    var bs = document.querySelectorAll('.exowise-tema button[data-valor]');
    for (var i = 0; i < bs.length; i++) bs[i].setAttribute('aria-pressed', String(bs[i].getAttribute('data-valor') === pref));
  }

  function montarTodos() {
    var els = document.querySelectorAll('[data-exowise-tema]');
    for (var i = 0; i < els.length; i++) montar(els[i]);
  }

  // Se aplica ya (el script va en el <head>) para que la página no parpadee en el tema equivocado.
  aplicar(leer());

  // Si está en automático y el sistema cambia, se sigue al sistema.
  if (media) {
    var alCambiar = function () { if (leer() === 'auto') aplicar('auto'); };
    if (media.addEventListener) media.addEventListener('change', alCambiar); else if (media.addListener) media.addListener(alCambiar);
  }
  // Otra pestaña (u otra app del mismo dominio) cambió el tema: se sincroniza.
  window.addEventListener('storage', function (e) { if (e.key === CLAVE) aplicar(leer()); });

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', montarTodos); else montarTodos();

  window.ExowiseTema = { obtener: leer, efectivo: function () { return efectivo(leer()); }, fijar: guardar, montar: montar, textos: TEXTOS };
})();
