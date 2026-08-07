/* Vista en directo: vídeo MJPEG + detecciones que llegan por SignalR. */
(function () {
    "use strict";

    const state = window.IVZ || { camaras: [], estados: [], recientes: [] };
    if (!state.camaras.length) return;

    const video = document.getElementById("videoFeed");
    const tabs = document.getElementById("camTabs");
    const feedFaces = document.getElementById("feedFaces");
    const feedPlates = document.getElementById("feedPlates");
    const feedObjects = document.getElementById("feedObjects");
    const MAX_ITEMS = 30;

    let selected = state.camaras[0].id;

    /* ---------- Panel de detecciones ---------- */

    function severityClass(hit) {
        if (!hit.conocido) return "unknown";
        return hit.autorizado ? "known" : "restricted";
    }

    function badge(hit) {
        if (!hit.conocido) {
            if (hit.tipo === "matricula") return '<span class="badge badge-danger">No registrada</span>';
            if (hit.tipo === "objeto") return '<span class="badge badge-warn">Sin etiquetar</span>';
            return '<span class="badge badge-danger">Desconocido</span>';
        }
        return hit.autorizado
            ? '<span class="badge badge-ok">Autorizado</span>'
            : '<span class="badge badge-warn">No autorizado</span>';
    }

    function render(hit) {
        const item = document.createElement("div");
        item.className = "hit " + severityClass(hit);

        const isPlate = hit.tipo === "matricula";

        const img = document.createElement("img");
        img.alt = hit.etiqueta || "";
        if (isPlate) img.className = "thumb-plate";
        img.src = hit.miniatura || "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg'/%3E";

        const body = document.createElement("div");
        body.className = "hit-body";

        const title = document.createElement("div");
        title.className = "hit-title" + (isPlate ? " plate" : "");
        title.textContent = hit.etiqueta || "—";

        const meta = document.createElement("div");
        meta.className = "hit-meta";
        const bits = [hit.hora, hit.camara];
        if (isPlate) {
            bits.push("OCR " + hit.similitud + "%");
            if (hit.conocido && hit.detalle) bits.push(hit.detalle);
        } else if (hit.tipo === "objeto") {
            bits.push("Detección " + hit.confianza + "%");
            if (hit.detalle) bits.push(hit.detalle);
        } else if (hit.conocido) {
            bits.push("Similitud " + hit.similitud + "%");
        } else {
            bits.push("Detección " + hit.confianza + "%");
        }
        meta.textContent = bits.filter(Boolean).join(" · ");

        body.appendChild(title);
        body.appendChild(meta);

        const flag = document.createElement("div");
        flag.innerHTML = badge(hit);

        item.appendChild(img);
        item.appendChild(body);
        item.appendChild(flag);
        return item;
    }

    function push(hit) {
        if (hit.camaraId !== selected) return;

        const target = hit.tipo === "matricula" ? feedPlates
                     : hit.tipo === "objeto" ? feedObjects
                     : feedFaces;
        if (!target) return;

        target.insertBefore(render(hit), target.firstChild);
        while (target.children.length > MAX_ITEMS) target.removeChild(target.lastChild);
    }

    function repaintFeeds() {
        feedFaces.innerHTML = "";
        feedPlates.innerHTML = "";
        if (feedObjects) feedObjects.innerHTML = "";
        state.recientes
            .filter(h => h.camaraId === selected)
            .slice(0, MAX_ITEMS)
            .reverse()
            .forEach(push);
    }

    /* ---------- Estado de la cámara ---------- */

    function applyStatus(st) {
        if (!st || st.camaraId !== selected) return;

        const dot = document.getElementById("stDot");
        dot.className = "dot " + (st.conectada ? "on" : "off");

        document.getElementById("stState").textContent = st.estado || "—";
        document.getElementById("stRes").textContent = st.resolucion || "—";
        document.getElementById("stFps").textContent = st.fps ? st.fps.toFixed(1) : "—";
        document.getElementById("stFrames").textContent = st.fotogramas || 0;

        const err = document.getElementById("stError");
        if (st.error) {
            err.textContent = st.error;
            err.hidden = false;
        } else {
            err.hidden = true;
        }
    }

    function currentStatus() {
        return state.estados.find(s => s.camaraId === selected);
    }

    /* ---------- Selección de cámara ---------- */

    function select(id) {
        selected = id;

        Array.from(tabs.querySelectorAll(".cam-tab")).forEach(b =>
            b.classList.toggle("active", b.dataset.camera === id));

        // La barra temporal fuerza al navegador a abrir un MJPEG nuevo.
        video.src = "/stream/" + id + "?t=" + Date.now();

        repaintFeeds();
        applyStatus(currentStatus());
    }

    tabs.addEventListener("click", function (e) {
        const button = e.target.closest(".cam-tab");
        if (button) select(button.dataset.camera);
    });

    /* ---------- Conexión en tiempo real ---------- */

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/detecciones")
        .withAutomaticReconnect()
        .build();

    connection.on("deteccion", function (hit) {
        state.recientes.unshift(hit);
        if (state.recientes.length > 200) state.recientes.pop();
        push(hit);
    });

    connection.on("estadoCamara", function (st) {
        const i = state.estados.findIndex(x => x.camaraId === st.camaraId);
        if (i >= 0) state.estados[i] = st; else state.estados.push(st);
        applyStatus(st);
    });

    connection.start().catch(function (err) {
        console.error("No se pudo abrir el canal en tiempo real", err);
    });

    // Si el MJPEG se corta (reinicio de la cámara), se reintenta solo.
    video.addEventListener("error", function () {
        setTimeout(function () { video.src = "/stream/" + selected + "?t=" + Date.now(); }, 3000);
    });

    select(selected);
})();
