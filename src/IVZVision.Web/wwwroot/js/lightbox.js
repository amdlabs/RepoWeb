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
                  placa, img.dataset.evento);
            return;
        }

        abrir(img.src, img.alt || "");
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

    /// src = imagen principal; original = foto real debajo; placa/evento habilitan la corrección.
    function abrir(src, alt, original, placa, eventoId) {
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

        // La lectura original acompaña a la placa generada, para poder contrastarlas.
        if (original && original !== src) {
            var caja = document.createElement("div");
            caja.style.cssText = "display:flex;flex-direction:column;align-items:center;gap:14px;max-height:92vh";

            var miniatura = document.createElement("img");
            miniatura.src = original;
            miniatura.alt = "Lectura original";
            miniatura.style.cssText = "max-width:70vw;max-height:32vh;border-radius:8px";

            caja.appendChild(grande);
            caja.appendChild(miniatura);
            if (placa && eventoId) caja.appendChild(editorMatricula(placa, eventoId, grande));
            overlay.appendChild(caja);
        }
        else {
            if (placa && eventoId) {
                var caja2 = document.createElement("div");
                caja2.style.cssText = "display:flex;flex-direction:column;align-items:center;gap:14px";
                caja2.appendChild(grande);
                caja2.appendChild(editorMatricula(placa, eventoId, grande));
                overlay.appendChild(caja2);
            } else {
                overlay.appendChild(grande);
            }
        }

        overlay.appendChild(cerrar);
        document.body.appendChild(overlay);
    }
})();
