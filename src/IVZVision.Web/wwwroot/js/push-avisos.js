/* Activación de los avisos push en este dispositivo.

   El botón de la campana aparece cuando el navegador soporta push (en Android, la
   aplicación instalada). Al activarlos, el teléfono recibe cada rostro visto aunque
   la aplicación esté cerrada; al tocar el aviso se abre la foto completa y el vídeo
   en vivo de esa cámara. */
(function () {
    "use strict";

    if (!("serviceWorker" in navigator) || !("PushManager" in window) || !("Notification" in window)) return;

    var boton = document.getElementById("btnAvisosPush");
    if (!boton) return;

    boton.hidden = false;

    function pintar(activo) {
        boton.textContent = activo ? "🔔" : "🔕";
        boton.title = activo
            ? "Avisos push activados en este dispositivo (tocar para desactivar)"
            : "Activar avisos push en este dispositivo";
        boton.dataset.activo = activo ? "1" : "0";
    }

    // El formato de clave que exige el navegador no es el que viaja por JSON.
    function claveABytes(base64) {
        var relleno = "=".repeat((4 - (base64.length % 4)) % 4);
        var crudo = atob((base64 + relleno).replace(/-/g, "+").replace(/_/g, "/"));
        var bytes = new Uint8Array(crudo.length);
        for (var i = 0; i < crudo.length; i++) bytes[i] = crudo.charCodeAt(i);
        return bytes;
    }

    function datosDe(sub) {
        var json = sub.toJSON();
        return { endpoint: sub.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth };
    }

    navigator.serviceWorker.ready.then(function (reg) {
        reg.pushManager.getSubscription().then(function (sub) { pintar(!!sub); });

        boton.addEventListener("click", function () {
            reg.pushManager.getSubscription().then(function (sub) {
                if (sub) {
                    // Desactivar: baja en el servidor y en el navegador.
                    fetch("/api/push/baja", {
                        method: "POST",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify(datosDe(sub)),
                    }).catch(function () { });
                    sub.unsubscribe().finally(function () { pintar(false); });
                    return;
                }

                Notification.requestPermission().then(function (permiso) {
                    if (permiso !== "granted") return;

                    fetch("/api/push/clave")
                        .then(function (r) { return r.json(); })
                        .then(function (r) {
                            return reg.pushManager.subscribe({
                                userVisibleOnly: true,
                                applicationServerKey: claveABytes(r.clave),
                            });
                        })
                        .then(function (sub) {
                            return fetch("/api/push/suscribir", {
                                method: "POST",
                                headers: { "Content-Type": "application/json" },
                                body: JSON.stringify(datosDe(sub)),
                            });
                        })
                        .then(function () { pintar(true); })
                        .catch(function () { pintar(false); });
                });
            });
        });
    });
})();
