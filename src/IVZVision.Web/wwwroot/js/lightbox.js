/* Visor global de imágenes: doble clic sobre cualquier foto (recortes de eventos,
   rostros detectados, miniaturas) la abre ampliada. Se cierra con ✕, Esc o clic. */
(function () {
    "use strict";

    document.addEventListener("dblclick", function (e) {
        var img = e.target.closest("img");
        if (!img) return;

        // El vídeo en directo y el muro tienen su propio maximizador.
        if (img.id === "videoFeed") return;
        if (img.closest(".mon-cell") || img.closest(".mon-overlay") || img.closest(".lightbox")) return;
        if (!img.closest("main")) return;
        if (!img.src) return;

        // En una matrícula se muestra la placa dibujada con el texto leído, no el recorte.
        var placa = img.dataset.plate;
        if (placa) {
            abrir("/matricula/" + encodeURIComponent(placa) + ".svg", "Matrícula " + placa, img.src,
                  placa, img.dataset.evento, img.dataset.escena);
            return;
        }

        abrir(img.src, img.alt || "", null, null, null, img.dataset.escena);
    });

    /// Editor de la lectura: corrige la matrícula y el sistema aprende de ello.
    function editorMatricula(placa, eventoId, imagenPlaca) {
        var caja = document.createElement("div");
        caja.className = "card";
        caja.style.cssText = "max-width:420px;width:92vw;text-align:center";

        caja.innerHTML =
            '<div class="hint" style="margin-bottom:8px">¿La lectura es incorrecta? Corríjala y el sistema ' +
            'aprenderá: rectificará el histórico y aplicará la corrección en las próximas lecturas.</div>' +
            '<div class="actions" style="justify-content:center;gap:8px">' +
            '<input id="lbPlaca" value="' + placa + '" style="max-width:170px;text-transform:uppercase" />' +
            '<button class="btn small" id="lbGuardar" type="button">Corregir y aprender</button></div>' +
            '<div class="test-output" id="lbOut" hidden></div>';

        caja.addEventListener("dblclick", function (e) { e.stopPropagation(); });
        caja.addEventListener("click", function (e) { e.stopPropagation(); });

        caja.querySelector("#lbGuardar").addEventListener("click", function () {
            var out = caja.querySelector("#lbOut");
            var correcta = caja.querySelector("#lbPlaca").value.trim().toUpperCase();

            fetch("/api/correcciones/matricula", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ eventoId: Number(eventoId), correcta: correcta, aplicarAlHistorico: true })
            })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                out.hidden = false;
                out.className = "test-output " + (data.ok ? "ok" : "ko");
                out.textContent = data.mensaje;
                if (data.ok && data.matricula) {
                    // Se redibuja la placa con el texto corregido.
                    imagenPlaca.src = "/matricula/" + encodeURIComponent(data.matricula) + ".svg";
                    setTimeout(function () { window.location.reload(); }, 1800);
                }
            })
            .catch(function (err) {
                out.hidden = false;
                out.className = "test-output ko";
                out.textContent = "No se pudo corregir: " + err.message;
            });
        });

        return caja;
    }

    /// src = imagen principal; original = foto real debajo; placa/evento habilitan la
    /// corrección; escena = fotograma completo de contexto.
    function abrir(src, alt, original, placa, eventoId, escena) {
        var overlay = document.createElement("div");
        overlay.className = "mon-overlay lightbox";

        var grande = document.createElement("img");
        grande.className = "mon-overlay-video";
        grande.src = src;
        grande.alt = alt;

        var cerrar = document.createElement("button");
        cerrar.className = "mon-overlay-close";
        cerrar.type = "button";
        cerrar.title = "Cerrar (Esc)";
        cerrar.textContent = "✕";

        if (alt) {
            var titulo = document.createElement("div");
            titulo.className = "mon-overlay-title";
            titulo.textContent = alt;
            overlay.appendChild(titulo);
        }

        function close() {
            overlay.remove();
            document.removeEventListener("keydown", onKey);
        }

        function onKey(ev) { if (ev.key === "Escape") close(); }

        cerrar.addEventListener("click", close);
        overlay.addEventListener("click", function (ev) { if (ev.target === overlay) close(); });
        overlay.addEventListener("dblclick", close);
        document.addEventListener("keydown", onKey);

        // Todo se apila en una caja: detalle arriba, lectura original, escena
        // completa y, si procede, el editor de corrección.
        var caja = document.createElement("div");
        caja.className = "lb-pila";
        caja.appendChild(grande);

        if (original && original !== src) {
            var miniatura = document.createElement("img");
            miniatura.src = original;
            miniatura.alt = "Lectura original";
            miniatura.className = "lb-original";
            miniatura.title = "Foto de la detección";
            caja.appendChild(miniatura);
        }

        // Escena completa: dónde ocurrió, con los cuadrantes dibujados.
        if (escena) {
            var etiqueta = document.createElement("div");
            etiqueta.className = "hint";
            etiqueta.textContent = "Escena completa";
            caja.appendChild(etiqueta);

            var completa = document.createElement("img");
            completa.src = escena;
            completa.alt = "Escena completa";
            completa.className = "lb-escena";
            completa.title = "Fotograma completo de la cámara";
            caja.appendChild(completa);
        }

        if (placa && eventoId) caja.appendChild(editorMatricula(placa, eventoId, grande));

        overlay.appendChild(caja);
        overlay.appendChild(cerrar);
        document.body.appendChild(overlay);
    }
})();
