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

        abrir(img.src, img.alt || "");
    });

    function abrir(src, alt) {
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

        overlay.appendChild(grande);
        overlay.appendChild(cerrar);
        document.body.appendChild(overlay);
    }
})();
