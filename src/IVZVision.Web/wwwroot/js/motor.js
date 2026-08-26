/* Panel del motor de reconocimiento: la web está siempre accesible; esto enciende
   o apaga la captura y el análisis de las cámaras. El estado es persistente, así
   que se respeta también después de reiniciar el equipo. */
(function () {
    "use strict";

    var panel = document.getElementById("motorPanel");
    if (!panel) return;

    var dot = document.getElementById("motorDot");
    var estado = document.getElementById("motorEstado");
    var detalle = document.getElementById("motorDetalle");
    var btnEncender = document.getElementById("motorEncender");
    var btnApagar = document.getElementById("motorApagar");

    function pintar(m) {
        var arrancando = m.encendido && !m.enMarcha;

        dot.className = "dot " + (m.enMarcha ? "on" : m.encendido ? "warn" : "off");
        panel.classList.toggle("motor-apagado", !m.encendido);

        estado.textContent = m.enMarcha ? "Motor en marcha"
                           : arrancando ? "Motor encendido, arrancando cámaras…"
                           : "Motor apagado";

        detalle.textContent = m.enMarcha
            ? m.conectadas + " de " + m.camaras + " cámara(s) en directo · " + m.fps + " fps de media"
            : m.encendido
                ? "Las cámaras están conectándose."
                : "La web sigue accesible; no se captura ni se analiza. Seguirá apagado tras reiniciar.";

        if (btnEncender) btnEncender.hidden = m.encendido;
        if (btnApagar) btnApagar.hidden = !m.encendido;
    }

    function consultar() {
        fetch("/api/motor/estado")
            .then(function (r) { return r.json(); })
            .then(pintar)
            .catch(function () {
                estado.textContent = "No se pudo consultar el motor";
                dot.className = "dot off";
            });
    }

    function cambiar(encendido, boton) {
        var texto = boton.textContent;
        boton.disabled = true;
        boton.textContent = encendido ? "Encendiendo…" : "Apagando…";

        fetch("/api/motor", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ encendido: encendido })
        })
        .then(function (r) { return r.json(); })
        .then(function (data) {
            detalle.textContent = data.mensaje || "";
            // Las cámaras tardan unos segundos en arrancar: se consulta un par de veces.
            setTimeout(consultar, 1500);
            setTimeout(consultar, 6000);
        })
        .catch(function (err) {
            detalle.textContent = "No se pudo cambiar el estado: " + err.message;
        })
        .finally(function () {
            boton.disabled = false;
            boton.textContent = texto;
        });
    }

    if (btnEncender) btnEncender.addEventListener("click", function () { cambiar(true, btnEncender); });
    if (btnApagar) btnApagar.addEventListener("click", function () {
        if (confirm("¿Apagar el motor? Se dejarán de vigilar las cámaras hasta que lo encienda de nuevo (también tras reiniciar el equipo).")) {
            cambiar(false, btnApagar);
        }
    });

    consultar();
    setInterval(function () { if (!document.hidden) consultar(); }, 10000);
})();
