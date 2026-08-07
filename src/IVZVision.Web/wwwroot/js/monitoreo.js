/* Muro de monitoreo: cuadrícula de 4/6/8/12 cámaras con el vídeo MJPEG procesado.
   Los datos vienen del backend (/api/camaras); aquí sólo hay presentación. */
(function () {
    "use strict";

    var grid = document.getElementById("monGrid");
    var vacio = document.getElementById("monVacio");
    var btnPrev = document.getElementById("monAnterior");
    var btnNext = document.getElementById("monSiguiente");
    var lblPage = document.getElementById("monPagina");

    // columnas x filas por cada distribución
    var LAYOUTS = { 4: [2, 2], 6: [3, 2], 8: [4, 2], 12: [4, 3] };

    var camaras = [];
    var layout = Number(localStorage.getItem("ivz.monitoreo.layout")) || 4;
    var pagina = 0;

    // El muro NO abre un flujo MJPEG permanente por celda: el navegador limita las
    // conexiones simultáneas por servidor (~6) y con más cámaras la web entera se
    // bloquearía. Cada celda refresca su instantánea con peticiones cortas.
    var REFRESH_MS = 700;
    var timer = null;

    function celdas() { return layout; }
    function totalPaginas() { return Math.max(1, Math.ceil(camaras.length / celdas())); }

    function refreshCells() {
        if (document.hidden) return;
        if (maximizada) return; // con una cámara maximizada, el muro descansa
        grid.querySelectorAll("img[data-camara]").forEach(function (img) {
            if (img.dataset.cargando === "1") return; // aún descargando la anterior
            img.dataset.cargando = "1";
            img.src = "/stream/" + img.dataset.camara + "/instantanea?t=" + Date.now();
        });
    }

    /* ---------- Cámara maximizada (doble clic) ---------- */

    var maximizada = false;

    function maximizar(cam) {
        if (maximizada) return;
        maximizada = true;

        var overlay = document.createElement("div");
        overlay.className = "mon-overlay";

        var titulo = document.createElement("div");
        titulo.className = "mon-overlay-title";
        titulo.textContent = cam.nombre;

        var cerrar = document.createElement("button");
        cerrar.className = "mon-overlay-close";
        cerrar.type = "button";
        cerrar.title = "Cerrar (Esc)";
        cerrar.textContent = "✕";

        // Una sola cámara maximizada sí usa el vídeo MJPEG continuo.
        var video = document.createElement("img");
        video.className = "mon-overlay-video";
        video.alt = cam.nombre;
        video.src = "/stream/" + cam.id + "?t=" + Date.now();
        video.addEventListener("error", function () {
            if (!maximizada) return;
            setTimeout(function () { video.src = "/stream/" + cam.id + "?t=" + Date.now(); }, 3000);
        });

        function cerrarOverlay() {
            maximizada = false;
            video.src = ""; // corta el flujo MJPEG
            overlay.remove();
            document.removeEventListener("keydown", onKey);
            refreshCells(); // el muro continúa al instante
        }

        function onKey(e) { if (e.key === "Escape") cerrarOverlay(); }

        cerrar.addEventListener("click", cerrarOverlay);
        overlay.addEventListener("dblclick", cerrarOverlay);
        document.addEventListener("keydown", onKey);

        overlay.appendChild(video);
        overlay.appendChild(titulo);
        overlay.appendChild(cerrar);
        document.body.appendChild(overlay);
    }

    function render() {
        var cols = LAYOUTS[layout][0];
        grid.style.gridTemplateColumns = "repeat(" + cols + ", 1fr)";
        grid.innerHTML = "";

        pagina = Math.min(pagina, totalPaginas() - 1);
        var inicio = pagina * celdas();
        var visibles = camaras.slice(inicio, inicio + celdas());

        visibles.forEach(function (cam) {
            var cell = document.createElement("div");
            cell.className = "mon-cell";

            var img = document.createElement("img");
            img.alt = cam.nombre;
            img.dataset.camara = cam.id;
            img.addEventListener("load", function () { img.dataset.cargando = "0"; });
            img.addEventListener("error", function () { img.dataset.cargando = "0"; });
            img.src = "/stream/" + cam.id + "/instantanea?t=" + Date.now();

            var label = document.createElement("div");
            label.className = "mon-label";
            var dot = document.createElement("span");
            dot.className = "dot " + (cam.conectada ? "on" : "off");
            label.appendChild(dot);
            label.appendChild(document.createTextNode(" " + cam.nombre));

            cell.appendChild(img);
            cell.appendChild(label);

            // Doble clic: maximizar esa cámara sobre el muro (Esc o ✕ para volver).
            cell.addEventListener("dblclick", function () { maximizar(cam); });

            grid.appendChild(cell);
        });

        // Relleno para mantener la cuadrícula estable
        for (var i = visibles.length; i < celdas(); i++) {
            var empty = document.createElement("div");
            empty.className = "mon-cell mon-empty";
            empty.textContent = "—";
            grid.appendChild(empty);
        }

        var multi = totalPaginas() > 1;
        btnPrev.hidden = !multi;
        btnNext.hidden = !multi;
        lblPage.hidden = !multi;
        if (multi) lblPage.textContent = "Página " + (pagina + 1) + " de " + totalPaginas();

        document.querySelectorAll("[data-layout]").forEach(function (b) {
            b.classList.toggle("active", Number(b.dataset.layout) === layout);
        });
    }

    document.querySelectorAll("[data-layout]").forEach(function (b) {
        b.addEventListener("click", function () {
            layout = Number(b.dataset.layout);
            localStorage.setItem("ivz.monitoreo.layout", layout);
            pagina = 0;
            render();
        });
    });

    btnPrev.addEventListener("click", function () { if (pagina > 0) { pagina--; render(); } });
    btnNext.addEventListener("click", function () { if (pagina < totalPaginas() - 1) { pagina++; render(); } });

    fetch("/api/camaras")
        .then(function (r) { return r.json(); })
        .then(function (data) {
            camaras = (data || []).filter(function (c) { return c.habilitada; });
            if (!camaras.length) {
                vacio.hidden = false;
                return;
            }
            render();
            timer = setInterval(refreshCells, REFRESH_MS);
        })
        .catch(function (err) {
            console.error("No se pudo obtener la lista de cámaras", err);
            vacio.hidden = false;
        });

    window.addEventListener("beforeunload", function () { if (timer) clearInterval(timer); });
})();
