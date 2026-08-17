/* Instalación como aplicación de escritorio/móvil (PWA).
   Registra el service worker y muestra el botón «Instalar app» cuando el
   navegador considera que el sitio es instalable (Chrome, Edge, Brave…).
   En Safari la instalación es manual: Archivo → Añadir al Dock. */
(function () {
    "use strict";

    if ("serviceWorker" in navigator) {
        window.addEventListener("load", function () {
            navigator.serviceWorker.register("/sw.js").catch(function (err) {
                console.debug("Service worker no registrado:", err);
            });
        });
    }

    var prompt = null;

    function boton() { return document.getElementById("btnInstalarApp"); }

    // El navegador avisa cuando el sitio cumple los requisitos de instalación.
    window.addEventListener("beforeinstallprompt", function (e) {
        e.preventDefault();          // se pospone para lanzarlo desde nuestro botón
        prompt = e;
        var b = boton();
        if (b) b.hidden = false;
    });

    window.addEventListener("appinstalled", function () {
        prompt = null;
        var b = boton();
        if (b) b.hidden = true;
    });

    document.addEventListener("click", function (e) {
        var b = e.target.closest("#btnInstalarApp");
        if (!b) return;

        if (!prompt) {
            // Safari y iOS no exponen la API: se explica el gesto manual.
            var esApple = /Safari/.test(navigator.userAgent) && !/Chrome|Chromium|Edg/.test(navigator.userAgent);
            alert(esApple
                ? "En Safari: menú Archivo → «Añadir al Dock» (macOS), o Compartir → «Añadir a pantalla de inicio» (iPhone/iPad)."
                : "Use el icono de instalar de la barra de direcciones, o el menú del navegador → «Instalar Cerbero Garage».");
            return;
        }

        prompt.prompt();
        prompt.userChoice.then(function (resultado) {
            if (resultado.outcome === "accepted") {
                prompt = null;
                var btn = boton();
                if (btn) btn.hidden = true;
            }
        });
    });

    // Dentro de la app instalada el botón sobra.
    window.addEventListener("DOMContentLoaded", function () {
        var instalada = window.matchMedia("(display-mode: standalone)").matches
            || window.navigator.standalone === true;
        var b = boton();
        if (instalada && b) b.remove();
    });
})();
