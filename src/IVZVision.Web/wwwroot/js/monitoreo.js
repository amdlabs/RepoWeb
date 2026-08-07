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

    function celdas() { return layout; }
    function totalPaginas() { return Math.max(1, Math.ceil(camaras.length / celdas())); }

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
            img.src = "/stream/" + cam.id + "?t=" + Date.now();
            // Si el flujo se corta (reinicio de la cámara) se reengancha solo.
            img.addEventListener("error", function () {
                setTimeout(function () { img.src = "/stream/" + cam.id + "?t=" + Date.now(); }, 3000);
            });

            var label = document.createElement("div");
            label.className = "mon-label";
            var dot = document.createElement("span");
            dot.className = "dot " + (cam.conectada ? "on" : "off");
            label.appendChild(dot);
            label.appendChild(document.createTextNode(" " + cam.nombre));

            cell.appendChild(img);
            cell.appendChild(label);

            // Doble clic: abrir la vista en directo de esa cámara.
            cell.addEventListener("dblclick", function () { window.location.href = "/"; });

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
        })
        .catch(function (err) {
            console.error("No se pudo obtener la lista de cámaras", err);
            vacio.hidden = false;
        });
})();
