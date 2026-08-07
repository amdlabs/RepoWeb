/* Registro del service worker: habilita "Instalar aplicación" en Chrome/Edge
   y "Añadir a pantalla de inicio / al Dock" en Safari. */
(function () {
    "use strict";
    if ("serviceWorker" in navigator) {
        window.addEventListener("load", function () {
            navigator.serviceWorker.register("/sw.js").catch(function (err) {
                console.debug("Service worker no registrado:", err);
            });
        });
    }
})();
