/* Dashboard en tiempo real: cada detección de matrícula que llega por SignalR
   refresca los contadores y la tabla desde /api/dashboard/resumen. */
(function () {
    "use strict";

    var tabla = document.getElementById("dashVehiculos");
    if (!tabla) return;

    var actualizado = document.getElementById("dashActualizado");
    var pendiente = null;

    function badge(clase, texto) {
        return '<span class="badge ' + clase + '">' + texto + "</span>";
    }

    function fecha(iso) {
        var d = new Date(iso);
        return ("0" + d.getDate()).slice(-2) + "/" + ("0" + (d.getMonth() + 1)).slice(-2) +
               " " + ("0" + d.getHours()).slice(-2) + ":" + ("0" + d.getMinutes()).slice(-2);
    }

    function pintar(resumen) {
        document.querySelectorAll("[data-campo]").forEach(function (el) {
            var v = resumen[el.dataset.campo];
            if (v !== undefined) el.textContent = v;
        });

        tabla.innerHTML = "";
        (resumen.ultimosVehiculos || []).forEach(function (v) {
            var tr = document.createElement("tr");
            tr.innerHTML =
                '<td class="plate"><b></b></td>' +
                "<td></td>" +
                "<td>" + (v.yaVistoAntes ? badge("badge-warn", "ya visto antes") : badge("badge-muted", "primera vez")) + "</td>" +
                "<td>" + v.vecesVisto + "</td>" +
                "<td>" + fecha(v.primeraVez) + "</td>" +
                "<td>" + fecha(v.ultimaVez) + "</td>" +
                "<td></td>";
            tr.children[0].firstChild.textContent = v.matricula;
            tr.children[1].textContent = v.etiqueta;
            if (v.registrado) tr.children[1].innerHTML += " " + badge("badge-ok", "registrado");
            tr.children[6].textContent = v.ultimaCamara || "";
            tabla.appendChild(tr);
        });

        actualizado.textContent = "Actualizado a las " + new Date().toLocaleTimeString();
    }

    function refrescar() {
        // Amortigua ráfagas: varias detecciones seguidas = un solo refresco.
        if (pendiente) return;
        pendiente = setTimeout(function () {
            pendiente = null;
            fetch("/api/dashboard/resumen")
                .then(function (r) { return r.json(); })
                .then(pintar)
                .catch(function (err) { console.error("No se pudo refrescar el dashboard", err); });
        }, 800);
    }

    var connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/detecciones")
        .withAutomaticReconnect()
        .build();

    connection.on("deteccion", function (hit) {
        // Los vehículos refrescan al instante; el resto de tipos también cuentan en los totales.
        refrescar();
    });

    connection.start().catch(function (err) {
        console.error("No se pudo abrir el canal en tiempo real", err);
    });

    // Red de seguridad: refresco periódico aunque no lleguen eventos.
    setInterval(function () { if (!document.hidden) refrescar(); }, 60000);
})();
