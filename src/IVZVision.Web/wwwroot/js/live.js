/* Vista en directo: vídeo MJPEG + detecciones que llegan por SignalR. */
(function () {
    "use strict";

    const state = window.IVZ || { camaras: [], estados: [], recientes: [] };
    if (!state.camaras.length) return;

    const video = document.getElementById("videoFeed");
    const picker = document.getElementById("camSelect");
    const kinds = document.getElementById("camKinds");

    const feeds = {
        alerta: document.getElementById("feedAlerts"),
        rostro: document.getElementById("feedFaces"),
        matricula: document.getElementById("feedPlates"),
        otros: document.getElementById("feedOthers")
    };

    const MAX_ITEMS = 25;

    let selected = state.camaras[0].id;

    /* ---------- Panel de detecciones ---------- */

    function feedFor(hit) {
        if (hit.tipo === "alerta") return feeds.alerta;
        if (hit.tipo === "rostro") return feeds.rostro;
        if (hit.tipo === "matricula") return feeds.matricula;
        return feeds.otros;
    }

    function severityClass(hit) {
        if (hit.tipo === "alerta") return hit.gravedad === "Info" ? "restricted" : "unknown";
        if (!hit.conocido) return "unknown";
        return hit.autorizado ? "known" : "restricted";
    }

    function badge(hit) {
        if (hit.tipo === "alerta") {
            const label = { Info: "aviso", Warning: "atención", Critical: "crítica" }[hit.gravedad] || "alerta";
            const cls = hit.gravedad === "Info" ? "badge-warn" : "badge-danger";
            return '<span class="badge ' + cls + '">' + label + "</span>";
        }
        if (hit.tipo === "codigo" || hit.tipo === "texto") {
            return '<span class="badge badge-muted">leído</span>';
        }
        if (!hit.conocido) {
            return hit.tipo === "matricula"
                ? '<span class="badge badge-danger">No registrada</span>'
                : '<span class="badge badge-danger">Sin identificar</span>';
        }
        return hit.autorizado
            ? '<span class="badge badge-ok">Autorizado</span>'
            : '<span class="badge badge-warn">No autorizado</span>';
    }

    function metaFor(hit) {
        const bits = [hit.hora, hit.camara];

        switch (hit.tipo) {
            case "alerta":
                if (hit.motivo) bits.push(hit.motivo);
                break;
            case "matricula":
                bits.push("OCR " + hit.similitud + "%");
                if (hit.conocido && hit.detalle) bits.push(hit.detalle);
                break;
            case "rostro":
                bits.push(hit.conocido ? "Similitud " + hit.similitud + "%" : "Detección " + hit.confianza + "%");
                break;
            default:
                if (hit.detalle) bits.push(hit.detalle);
                bits.push("Detección " + hit.confianza + "%");
        }

        return bits.filter(Boolean).join(" · ");
    }

    function render(hit) {
        const item = document.createElement("div");
        item.className = "hit " + severityClass(hit);

        const isPlate = hit.tipo === "matricula";

        if (hit.miniatura) {
            const img = document.createElement("img");
            img.alt = hit.etiqueta || "";
            if (isPlate) img.className = "thumb-plate";
            img.src = hit.miniatura;
            item.appendChild(img);
        }

        const body = document.createElement("div");
        body.className = "hit-body";

        const title = document.createElement("div");
        title.className = "hit-title" + (isPlate ? " plate" : "");
        title.textContent = hit.etiqueta || "—";

        const meta = document.createElement("div");
        meta.className = "hit-meta";
        meta.textContent = metaFor(hit);

        body.appendChild(title);
        body.appendChild(meta);

        const flag = document.createElement("div");
        flag.innerHTML = badge(hit);

        item.appendChild(body);
        item.appendChild(flag);
        return item;
    }

    function push(hit) {
        if (hit.camaraId !== selected) return;

        const target = feedFor(hit);
        if (!target) return;

        target.insertBefore(render(hit), target.firstChild);
        while (target.children.length > MAX_ITEMS) target.removeChild(target.lastChild);
    }

    function repaintFeeds() {
        Object.values(feeds).forEach(function (f) { if (f) f.innerHTML = ""; });

        state.recientes
            .filter(h => h.camaraId === selected)
            .slice(0, MAX_ITEMS * 2)
            .reverse()
            .forEach(push);
    }

    /* ---------- Estado de la cámara ---------- */

    function applyStatus(st) {
        if (!st || st.camaraId !== selected) return;

        document.getElementById("stDot").className = "dot " + (st.conectada ? "on" : "off");
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

    function describeKinds(camera) {
        if (!camera || !camera.reconoce) return "";

        const etiquetas = {
            rostros: "rostros", matriculas: "matrículas", objetos: "objetos",
            codigos: "códigos", texto: "texto", actividad: "actividad"
        };

        const activos = Object.keys(etiquetas).filter(k => camera.reconoce[k]).map(k => etiquetas[k]);
        return activos.length ? "Reconoce: " + activos.join(", ") : "Sin reconocimiento activado";
    }

    function select(id) {
        selected = id;

        const camera = state.camaras.find(c => c.id === id);
        if (kinds) kinds.textContent = describeKinds(camera);
        if (picker && picker.value !== id) picker.value = id;

        // La marca temporal fuerza al navegador a abrir un MJPEG nuevo.
        video.src = "/stream/" + id + "?t=" + Date.now();

        repaintFeeds();
        applyStatus(currentStatus());
    }

    if (picker) {
        picker.addEventListener("change", function () { select(picker.value); });
    }

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
