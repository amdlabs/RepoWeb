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
    var LAYOUTS = { 1: [1, 1], 4: [2, 2], 6: [3, 2], 8: [4, 2], 12: [4, 3] };

    var camaras = [];
    var layout = Number(localStorage.getItem("ivz.monitoreo.layout")) || 4;
    if (!LAYOUTS[layout]) layout = 4;
    var pagina = 0;

    /* ---------- Carrusel: rotación automática de páginas ---------- */

    var carruselBtn = document.getElementById("monCarrusel");
    var carruselSeg = document.getElementById("monCarruselSeg");
    var carruselTimer = null;

    function carruselActivo() { return carruselTimer !== null; }

    function pasoCarrusel() {
        if (document.hidden || maximizada) return;
        if (totalPaginas() < 2) return;
        pagina = (pagina + 1) % totalPaginas();
        render();
    }

    function iniciarCarrusel() {
        detenerCarrusel();
        var seg = Math.max(3, Number(carruselSeg.value) || 10);
        carruselTimer = setInterval(pasoCarrusel, seg * 1000);
        carruselBtn.textContent = "⏸ Parar";
        carruselBtn.classList.add("active");
        localStorage.setItem("ivz.monitoreo.carrusel", "1");
        localStorage.setItem("ivz.monitoreo.carruselSeg", String(seg));
    }

    function detenerCarrusel() {
        if (carruselTimer) clearInterval(carruselTimer);
        carruselTimer = null;
        carruselBtn.textContent = "▶ Activar";
        carruselBtn.classList.remove("active");
        localStorage.setItem("ivz.monitoreo.carrusel", "0");
    }

    carruselBtn.addEventListener("click", function () {
        if (carruselActivo()) detenerCarrusel(); else iniciarCarrusel();
    });

    carruselSeg.addEventListener("change", function () {
        if (carruselActivo()) iniciarCarrusel(); // reinicia con el nuevo intervalo
    });

    /* ---------- Reparto inteligente de los feeds ----------
       El navegador permite ~6 conexiones HTTP/1.1 simultáneas por servidor.
       Estrategia: usar siempre el máximo de flujos MJPEG en vivo posibles.
       - ≤6 celdas visibles → TODAS con vídeo continuo (máximos fps).
       - >6 visibles (HTTP/1.1) → 5 flujos en vivo que van ROTANDO entre las
         celdas cada pocos segundos; el resto refresca instantáneas (se ven todas).
       - HTTPS usa HTTP/2 (multiplexado, sin límite) → todas en vivo siempre. */
    var HTTP2 = location.protocol === "https:";
    var MAX_LIVE_H1 = 6;      // todas en vivo si caben
    var LIVE_WHEN_ROTATING = 5; // deja 1 conexión libre para las instantáneas
    var ROTATE_MS = 12000;
    var REFRESH_MS = 700;
    var timer = null;
    var rotateTimer = null;
    var liveOffset = 0;

    function celdas() { return layout; }

    /* La vista es el orden en que se colocan las cámaras en el muro. Mientras nadie
       la toque es el orden natural; al editarla se guarda la lista de identificadores
       en este navegador. Las cámaras que la vista no menciona van detrás, para que
       una cámara nueva no quede inalcanzable. */
    var CLAVE_VISTA = "ivz.monitoreo.vista";
    var editando = false;

    function vistaGuardada() {
        try {
            var crudo = localStorage.getItem(CLAVE_VISTA);
            var lista = crudo ? JSON.parse(crudo) : null;
            return Array.isArray(lista) ? lista : null;
        } catch (e) {
            return null;
        }
    }

    function guardarVista(lista) {
        try {
            if (lista) localStorage.setItem(CLAVE_VISTA, JSON.stringify(lista));
            else localStorage.removeItem(CLAVE_VISTA);
        } catch (e) { /* sin memoria: la vista dura lo que la sesión */ }
    }

    /// Cámaras en el orden en que deben verse; los huecos vacíos son null.
    function ordenadas() {
        var vista = vistaGuardada();
        if (!vista) return camaras.slice();

        var porId = {};
        camaras.forEach(function (c) { porId[c.id] = c; });

        var lista = vista.map(function (id) { return id ? (porId[id] || null) : null; });

        // Las que la vista no nombra se añaden al final para no perderlas de vista.
        var puestas = {};
        vista.forEach(function (id) { if (id) puestas[id] = true; });
        camaras.forEach(function (c) { if (!puestas[c.id]) lista.push(c); });

        return lista;
    }

    function totalPaginas() { return Math.max(1, Math.ceil(ordenadas().length / celdas())); }

    /* ---------- Vídeo por WebSocket: streaming real en TODAS las celdas ----------
       Un único WebSocket multiplexa los fotogramas de todas las cámaras visibles
       (no cuenta en el límite de conexiones HTTP). Si falla, se usa el reparto
       HTTP de más abajo como reserva. */
    var ws = null;
    var wsActivo = false;

    function startWs() {
        stopWs();
        var visibles = Array.prototype.slice.call(grid.querySelectorAll("img[data-camara]"))
            .map(function (i) { return i.dataset.camara; });
        if (!visibles.length || !("WebSocket" in window)) return;

        var proto = location.protocol === "https:" ? "wss://" : "ws://";
        try { ws = new WebSocket(proto + location.host + "/ws/video?camaras=" + visibles.join(",")); }
        catch (e) { ws = null; return; }

        ws.binaryType = "arraybuffer";

        ws.onopen = function () {
            wsActivo = true;
            // Las celdas dejan las conexiones HTTP: todo llega por el WebSocket.
            grid.querySelectorAll("img[data-camara]").forEach(function (img) {
                img.dataset.modo = "ws";
            });
        };

        ws.onmessage = function (e) {
            if (maximizada) return; // el muro descansa con una cámara maximizada
            var buf = e.data;
            var id = new TextDecoder().decode(buf.slice(0, 36));
            var img = grid.querySelector('img[data-camara="' + id + '"]');
            if (!img) return;

            var url = URL.createObjectURL(new Blob([buf.slice(36)], { type: "image/jpeg" }));
            var anterior = img.dataset.blob;
            img.dataset.blob = url;
            img.src = url;
            if (anterior) setTimeout(function () { URL.revokeObjectURL(anterior); }, 1000);
        };

        ws.onclose = ws.onerror = function () {
            if (!wsActivo && ws) { ws = null; assignFeeds(); return; } // nunca llegó a abrir: reserva HTTP
            wsActivo = false;
            ws = null;
            assignFeeds(); // sigue en HTTP mientras tanto
            setTimeout(function () { if (!maximizada) startWs(); }, 4000);
        };
    }

    function stopWs() {
        wsActivo = false;
        if (ws) { try { ws.onclose = null; ws.close(); } catch (e) { } ws = null; }
    }

    /// Decide qué celdas llevan vídeo en vivo y cuáles instantáneas (reserva sin WebSocket).
    function assignFeeds() {
        if (wsActivo) return;
        var imgs = Array.prototype.slice.call(grid.querySelectorAll("img[data-camara]"));
        if (!imgs.length) return;

        var liveCount = HTTP2 ? imgs.length
                      : imgs.length <= MAX_LIVE_H1 ? imgs.length
                      : LIVE_WHEN_ROTATING;

        imgs.forEach(function (img, i) {
            // La ventana de celdas "en vivo" gira con liveOffset.
            var vivo = ((i - liveOffset) % imgs.length + imgs.length) % imgs.length < liveCount;
            var modo = vivo ? "live" : "poll";
            if (img.dataset.modo === modo) return;

            img.dataset.modo = modo;
            img.dataset.cargando = "0";
            img.src = vivo
                ? "/stream/" + img.dataset.camara + "?t=" + Date.now()
                : "/stream/" + img.dataset.camara + "/instantanea?t=" + Date.now();
        });
    }

    function rotarFeeds() {
        if (wsActivo || document.hidden || maximizada) return;
        var visibles = grid.querySelectorAll("img[data-camara]").length;
        var liveCount = HTTP2 || visibles <= MAX_LIVE_H1 ? visibles : LIVE_WHEN_ROTATING;
        if (liveCount >= visibles) return; // todas en vivo: nada que rotar
        liveOffset = (liveOffset + 1) % visibles;
        assignFeeds();
    }

    function refreshCells() {
        if (wsActivo || document.hidden) return;
        if (maximizada) return; // con una cámara maximizada, el muro descansa
        grid.querySelectorAll('img[data-camara][data-modo="poll"]').forEach(function (img) {
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

        // Se sueltan los flujos del muro (WebSocket incluido) para que la cámara
        // maximizada tenga la conexión garantizada y todo el ancho de banda.
        stopWs();
        grid.querySelectorAll('img[data-camara]').forEach(function (img) {
            img.dataset.modo = "poll";
            img.dataset.cargando = "0";
            img.src = "/stream/" + img.dataset.camara + "/instantanea?t=" + Date.now();
        });

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

            // Se sale de la pantalla completa del navegador si la habíamos pedido.
            if (document.fullscreenElement) {
                var salida = document.exitFullscreen();
                if (salida && salida.catch) salida.catch(function () { });
            }

            overlay.remove();
            document.removeEventListener("keydown", onKey);
            document.removeEventListener("fullscreenchange", onFullscreenChange);
            assignFeeds();  // reserva HTTP inmediata…
            startWs();      // …y el streaming WebSocket vuelve al instante
        }

        function onKey(e) { if (e.key === "Escape") cerrarOverlay(); }

        // Salir de pantalla completa (con Esc o el gesto del navegador) cierra la vista.
        function onFullscreenChange() {
            if (!document.fullscreenElement && maximizada) cerrarOverlay();
        }

        cerrar.addEventListener("click", cerrarOverlay);
        overlay.addEventListener("dblclick", cerrarOverlay);
        document.addEventListener("keydown", onKey);
        document.addEventListener("fullscreenchange", onFullscreenChange);

        overlay.appendChild(video);
        overlay.appendChild(titulo);
        overlay.appendChild(cerrar);
        document.body.appendChild(overlay);

        // Pantalla completa real del navegador (oculta pestañas y barra de direcciones).
        // Si el navegador la deniega, queda el overlay a tamaño de ventana.
        if (overlay.requestFullscreen) {
            var peticion = overlay.requestFullscreen({ navigationUI: "hide" });
            if (peticion && peticion.catch) peticion.catch(function () { });
        }
        else if (overlay.webkitRequestFullscreen) {
            overlay.webkitRequestFullscreen(); // Safari
        }
    }

    function render() {
        var cols = LAYOUTS[layout][0];
        grid.style.gridTemplateColumns = "repeat(" + cols + ", 1fr)";
        grid.innerHTML = "";

        pagina = Math.min(pagina, totalPaginas() - 1);
        var inicio = pagina * celdas();
        var listado = ordenadas();
        var visibles = listado.slice(inicio, inicio + celdas());

        // En edición se dibujan siempre todos los recuadros, incluidos los vacíos,
        // para poder asignarles una cámara.
        if (editando) {
            while (visibles.length < celdas()) visibles.push(null);
        }

        visibles.forEach(function (cam, indice) {
            var posicion = inicio + indice;

            if (!cam) {
                var hueco = document.createElement("div");
                hueco.className = "mon-cell mon-empty";
                hueco.appendChild(selectorCamara(posicion, null));
                grid.appendChild(hueco);
                return;
            }

            var cell = document.createElement("div");
            cell.className = "mon-cell";

            if (editando) cell.appendChild(selectorCamara(posicion, cam.id));

            var img = document.createElement("img");
            img.alt = cam.nombre;
            img.dataset.camara = cam.id;
            img.addEventListener("load", function () { img.dataset.cargando = "0"; });
            img.addEventListener("error", function () {
                img.dataset.cargando = "0";
                // Un flujo en vivo cortado se reengancha solo.
                if (img.dataset.modo === "live") {
                    setTimeout(function () {
                        if (img.dataset.modo === "live" && !maximizada)
                            img.src = "/stream/" + cam.id + "?t=" + Date.now();
                    }, 3000);
                }
            });

            // Capa donde se pintan los recuadros de lo que se va reconociendo.
            var marcas = document.createElement("div");
            marcas.className = "mon-marcas";
            marcas.dataset.camara = cam.id;
            cell.appendChild(marcas);

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
        for (var i = editando ? celdas() : visibles.length; i < celdas(); i++) {
            var empty = document.createElement("div");
            empty.className = "mon-cell mon-empty";
            empty.textContent = "—";
            grid.appendChild(empty);
        }

        assignFeeds();
        startWs(); // streaming real por WebSocket para todas las celdas visibles

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
            rotateTimer = setInterval(rotarFeeds, ROTATE_MS);

            // Restaurar el carrusel tal y como quedó la última vez.
            var seg = localStorage.getItem("ivz.monitoreo.carruselSeg");
            if (seg && carruselSeg.querySelector('option[value="' + seg + '"]')) carruselSeg.value = seg;
            if (localStorage.getItem("ivz.monitoreo.carrusel") === "1") iniciarCarrusel();
        })
        .catch(function (err) {
            console.error("No se pudo obtener la lista de cámaras", err);
            vacio.hidden = false;
        });

    window.addEventListener("beforeunload", function () {
        if (timer) clearInterval(timer);
        if (rotateTimer) clearInterval(rotateTimer);
        stopWs();
    });

    /* ---------- Edición de la vista: qué cámara va en cada recuadro ---------- */

    function selectorCamara(posicion, idActual) {
        var caja = document.createElement("div");
        caja.className = "mon-selector";

        var sel = document.createElement("select");

        var vacio = document.createElement("option");
        vacio.value = "";
        vacio.textContent = "— vacío —";
        sel.appendChild(vacio);

        camaras.forEach(function (c) {
            var op = document.createElement("option");
            op.value = c.id;
            op.textContent = c.nombre;
            if (c.id === idActual) op.selected = true;
            sel.appendChild(op);
        });

        sel.addEventListener("change", function () {
            var vista = vistaGuardada();

            // La primera edición parte del orden que se está viendo.
            if (!vista) vista = ordenadas().map(function (c) { return c ? c.id : null; });

            while (vista.length <= posicion) vista.push(null);

            // Una cámara no puede estar dos veces: se libera su hueco anterior.
            var nuevo = sel.value || null;
            if (nuevo) {
                for (var i = 0; i < vista.length; i++) {
                    if (i !== posicion && vista[i] === nuevo) vista[i] = null;
                }
            }

            vista[posicion] = nuevo;
            guardarVista(vista);
            render();
        });

        // El clic en el desplegable no debe maximizar la celda.
        caja.addEventListener("dblclick", function (e) { e.stopPropagation(); });
        caja.addEventListener("click", function (e) { e.stopPropagation(); });

        caja.appendChild(sel);
        return caja;
    }

    var btnEditar = document.getElementById("monEditar");
    if (btnEditar) {
        btnEditar.addEventListener("click", function () {
            editando = !editando;
            btnEditar.textContent = editando ? "Terminar edición" : "Editar vista";
            btnEditar.classList.toggle("secondary", !editando);
            grid.classList.toggle("mon-editando", editando);
            render();
        });

        // Con la vista personalizada, un segundo botón permite volver al orden natural.
        var btnRestablecer = document.createElement("button");
        btnRestablecer.type = "button";
        btnRestablecer.className = "btn small secondary";
        btnRestablecer.textContent = "Orden por defecto";
        btnRestablecer.title = "Olvidar la vista personalizada";
        btnRestablecer.addEventListener("click", function () {
            guardarVista(null);
            render();
        });
        btnEditar.parentNode.insertBefore(btnRestablecer, btnEditar.nextSibling);
    }

    /* ---------- Recuadro con el nombre sobre la imagen en vivo ---------- */

    /* La imagen se muestra con object-fit: contain, así que dentro de la celda
       sobra espacio por arriba o por los lados. Para que el recuadro caiga sobre
       la cara hay que calcular dónde está de verdad la imagen dentro de la celda. */
    function rectoDeImagen(img) {
        var anchoCaja = img.clientWidth, altoCaja = img.clientHeight;
        var anchoReal = img.naturalWidth, altoReal = img.naturalHeight;

        if (!anchoReal || !altoReal) return { x: 0, y: 0, ancho: anchoCaja, alto: altoCaja };

        var escala = Math.min(anchoCaja / anchoReal, altoCaja / altoReal);
        var ancho = anchoReal * escala, alto = altoReal * escala;

        return { x: (anchoCaja - ancho) / 2, y: (altoCaja - alto) / 2, ancho: ancho, alto: alto };
    }

    function marcarDeteccion(d) {
        if (!d || d.cajaAncho == null || d.cajaAlto == null) return;

        var capa = grid.querySelector('.mon-marcas[data-camara="' + d.camaraId + '"]');
        if (!capa) return;

        var img = capa.parentNode.querySelector("img[data-camara]");
        if (!img) return;

        var r = rectoDeImagen(img);

        var marca = document.createElement("div");
        marca.className = "mon-marca" + (d.conocido ? " conocida" : "");
        marca.style.left = (r.x + (d.cajaX / 100) * r.ancho) + "px";
        marca.style.top = (r.y + (d.cajaY / 100) * r.alto) + "px";
        marca.style.width = ((d.cajaAncho / 100) * r.ancho) + "px";
        marca.style.height = ((d.cajaAlto / 100) * r.alto) + "px";

        // El nombre sólo aporta cuando se sabe de quién es.
        if (d.conocido && d.etiqueta) {
            var nombre = document.createElement("span");
            nombre.className = "mon-marca-nombre";
            nombre.textContent = d.etiqueta;
            marca.appendChild(nombre);
        }

        capa.appendChild(marca);

        // Cada marca dura lo justo: la siguiente vuelta del análisis vuelve a pintar.
        setTimeout(function () {
            if (marca.parentNode) marca.parentNode.removeChild(marca);
        }, 2500);
    }

    /* ---------- Paneles de detecciones en tiempo real (SignalR) ---------- */

    (function () {
        var feedFaces = document.getElementById("feedFaces");
        var feedPlates = document.getElementById("feedPlates");
        var feedObjects = document.getElementById("feedObjects");
        if (!feedFaces || typeof signalR === "undefined") return;

        var MAX_ITEMS = 30;

        function badge(hit) {
            if (hit.tipo === "texto") return '<span class="badge badge-muted">Texto leído</span>';
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
            var item = document.createElement("div");
            item.className = "hit " + (hit.tipo === "texto" ? "known"
                : !hit.conocido ? "unknown" : hit.autorizado ? "known" : "restricted");

            var img = document.createElement("img");
            img.alt = hit.etiqueta || "";
            if (hit.tipo === "matricula") img.className = "thumb-plate";
            img.src = hit.miniatura || "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg'/%3E";
            // Doble clic: recorte ampliado y escena completa de la detección.
            if (hit.escena) img.dataset.escena = hit.escena;
            if (hit.matricula) { img.dataset.plate = hit.matricula; if (hit.eventoId) img.dataset.evento = hit.eventoId; }

            var body = document.createElement("div");
            body.className = "hit-body";

            var title = document.createElement("div");
            title.className = "hit-title" + (hit.tipo === "matricula" ? " plate" : "");
            title.textContent = hit.etiqueta || "—";

            var meta = document.createElement("div");
            meta.className = "hit-meta";
            meta.textContent = [hit.hora, hit.camara].filter(Boolean).join(" · ");

            body.appendChild(title);
            body.appendChild(meta);

            var flag = document.createElement("div");
            flag.innerHTML = badge(hit);

            item.appendChild(img);
            item.appendChild(body);
            item.appendChild(flag);

            // Los rostros ya agrupados se pueden marcar para unirlos o etiquetarlos
            // sin salir del muro.
            if (hit.tipo === "rostro" && hit.grupoId) {
                item.dataset.grupo = hit.grupoId;

                var marca = document.createElement("input");
                marca.type = "checkbox";
                marca.className = "hit-marca";
                marca.title = "Marcar este rostro";
                marca.addEventListener("click", function (e) { e.stopPropagation(); });
                marca.addEventListener("change", refrescarSeleccion);
                item.insertBefore(marca, item.firstChild);
            }

            return item;
        }

        /* ---- Agrupar y etiquetar rostros desde el propio muro ---- */

        var btnAgrupar = document.getElementById("btnAgruparRostros");
        var btnEtiquetar = document.getElementById("btnEtiquetarRostro");
        var lblSeleccion = document.getElementById("rostrosSeleccion");

        /// Grupos distintos entre los rostros marcados (uno puede repetirse en varias fotos).
        function gruposMarcados() {
            var vistos = {};
            var lista = [];

            Array.prototype.slice.call(feedFaces.querySelectorAll(".hit-marca:checked"))
                .forEach(function (c) {
                    var id = parseInt(c.parentNode.dataset.grupo, 10);
                    if (!id || vistos[id]) return;
                    vistos[id] = true;
                    lista.push(id);
                });

            return lista;
        }

        function refrescarSeleccion() {
            var grupos = gruposMarcados();

            if (btnAgrupar) btnAgrupar.disabled = grupos.length < 2;
            if (btnEtiquetar) btnEtiquetar.disabled = grupos.length !== 1;

            if (!lblSeleccion) return;
            lblSeleccion.textContent = grupos.length === 0
                ? "marque los rostros que sean la misma persona"
                : grupos.length === 1
                    ? "1 rostro marcado: puede etiquetarlo"
                    : grupos.length + " rostros marcados: puede agruparlos";
        }

        function avisar(texto, error) {
            if (!lblSeleccion) return;
            lblSeleccion.textContent = texto;
            lblSeleccion.className = error ? "badge badge-danger" : "badge badge-ok";
            setTimeout(function () {
                lblSeleccion.className = "hint";
                refrescarSeleccion();
            }, 6000);
        }

        function pedir(url, cuerpo, alTerminar) {
            fetch(url, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(cuerpo),
            })
                .then(function (r) { return r.json().then(function (j) { return { ok: r.ok, j: j }; }); })
                .then(function (res) {
                    avisar(res.j.mensaje || (res.ok ? "Hecho." : "No se pudo completar."), !res.ok);
                    if (res.ok && alTerminar) alTerminar(res.j);
                })
                .catch(function () { avisar("No se pudo contactar con el servidor.", true); });
        }

        function desmarcarTodo() {
            Array.prototype.slice.call(feedFaces.querySelectorAll(".hit-marca:checked"))
                .forEach(function (c) { c.checked = false; });
        }

        if (btnAgrupar) {
            btnAgrupar.addEventListener("click", function () {
                var grupos = gruposMarcados();
                if (grupos.length < 2) return;

                btnAgrupar.disabled = true;
                pedir("/api/rostros/unificar", { grupos: grupos }, function () { desmarcarTodo(); });
            });
        }

        if (btnEtiquetar) {
            btnEtiquetar.addEventListener("click", function () {
                var grupos = gruposMarcados();
                if (grupos.length !== 1) return;

                var nombre = window.prompt("Nombre de esta persona:");
                if (!nombre) return;

                btnEtiquetar.disabled = true;
                pedir("/api/rostros/etiquetar", { grupoId: grupos[0], nombre: nombre },
                      function () { desmarcarTodo(); });
            });
        }

        function push(hit) {
            var target = hit.tipo === "matricula" ? feedPlates
                       : (hit.tipo === "objeto" || hit.tipo === "texto") ? feedObjects
                       : feedFaces;
            if (!target) return;
            var item = render(hit);
            item.dataset.hit = JSON.stringify(hit);
            if (!hit.conocido && hit.tipo !== "texto") {
                item.style.cursor = "pointer";
                item.title = "Clic para completar los datos de esta detección";
            }
            target.insertBefore(item, target.firstChild);
            while (target.children.length > MAX_ITEMS) target.removeChild(target.lastChild);
        }

        /* ---- Diálogo para completar los datos de una detección ---- */

        function abrirDialogo(hit) {
            var overlay = document.createElement("div");
            overlay.className = "mon-overlay";

            var card = document.createElement("div");
            card.className = "card dlg-card";

            var titulo = hit.tipo === "matricula" ? "Registrar vehículo"
                       : hit.tipo === "objeto" ? "Categorizar objeto"
                       : "Dar de alta a la persona";

            var campos = "";
            if (hit.tipo === "matricula") {
                campos =
                    // Placa generada con los caracteres leídos, en formato uruguayo.
                    '<img src="/matricula/' + encodeURIComponent(hit.matricula || "") + '.svg" ' +
                    'alt="Matrícula ' + (hit.matricula || "") + '" style="width:100%;max-width:340px;display:block;margin:0 auto 12px" />' +
                    (hit.miniatura ? '<img src="' + hit.miniatura + '" alt="Lectura original" style="width:180px;border-radius:6px;display:block;margin:0 auto 12px" />' : "") +
                    '<div class="field"><label>Matrícula</label><input id="dlgMatricula" value="' + (hit.matricula || "") + '" /></div>' +
                    '<div class="field"><label>Marca</label><input id="dlgMarca" placeholder="(opcional)" /></div>' +
                    '<div class="field"><label>Modelo</label><input id="dlgModelo" placeholder="(opcional)" /></div>' +
                    '<label class="check"><input type="checkbox" id="dlgAutorizado" checked /> Autorizado</label>';
            } else if (hit.tipo === "objeto") {
                campos =
                    '<div class="field"><label>Clase detectada</label><input value="' + (hit.detalle || "") + '" disabled /></div>' +
                    '<div class="field"><label>Categoría real</label><input id="dlgNombre" placeholder="p. ej. Portón" /></div>' +
                    '<label class="check"><input type="checkbox" id="dlgAutorizado" checked /> Autorizado</label>';
            } else {
                campos =
                    (hit.miniatura ? '<img src="' + hit.miniatura + '" style="width:120px;border-radius:8px;display:block;margin:0 auto 10px" />' : "") +
                    '<div class="field"><label>Nombre de la persona</label><input id="dlgNombre" autofocus /></div>' +
                    '<div class="hint">Se creará con este rostro como plantilla, marcada como no autorizada.</div>';
            }

            card.innerHTML =
                "<h2>" + titulo + "</h2>" +
                '<div class="hint">' + [hit.hora, hit.camara].filter(Boolean).join(" · ") + "</div>" +
                campos +
                '<div class="test-output" id="dlgOut" hidden></div>' +
                '<div class="actions" style="margin-top:12px">' +
                '<button class="btn" id="dlgGuardar" type="button">Guardar</button>' +
                '<button class="btn secondary" id="dlgCancelar" type="button">Cancelar</button></div>';

            function cerrar() { overlay.remove(); document.removeEventListener("keydown", onKey); }
            function onKey(ev) { if (ev.key === "Escape") cerrar(); }
            document.addEventListener("keydown", onKey);

            overlay.addEventListener("click", function (ev) { if (ev.target === overlay) cerrar(); });
            card.querySelector("#dlgCancelar").addEventListener("click", cerrar);

            card.querySelector("#dlgGuardar").addEventListener("click", function () {
                var out = card.querySelector("#dlgOut");
                var url, payload;

                if (hit.tipo === "matricula") {
                    url = "/api/detecciones/vehiculo";
                    payload = {
                        matricula: card.querySelector("#dlgMatricula").value,
                        marca: card.querySelector("#dlgMarca").value,
                        modelo: card.querySelector("#dlgModelo").value,
                        autorizado: card.querySelector("#dlgAutorizado").checked
                    };
                } else if (hit.tipo === "objeto") {
                    url = "/api/detecciones/objeto";
                    payload = {
                        clase: hit.detalle || "",
                        nombre: card.querySelector("#dlgNombre").value,
                        autorizado: card.querySelector("#dlgAutorizado").checked
                    };
                } else {
                    url = "/api/detecciones/persona";
                    payload = { eventoId: hit.eventoId, nombre: card.querySelector("#dlgNombre").value };
                }

                fetch(url, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify(payload)
                })
                .then(function (r) { return r.json(); })
                .then(function (data) {
                    out.hidden = false;
                    out.className = "test-output " + (data.ok ? "ok" : "ko");
                    out.textContent = data.mensaje;
                    if (data.ok) setTimeout(cerrar, 1400);
                })
                .catch(function (err) {
                    out.hidden = false;
                    out.className = "test-output ko";
                    out.textContent = "No se pudo guardar: " + err.message;
                });
            });

            overlay.appendChild(card);
            document.body.appendChild(overlay);
        }

        document.addEventListener("click", function (e) {
            var item = e.target.closest(".hit");
            if (!item || !item.dataset.hit) return;
            var hit = JSON.parse(item.dataset.hit);
            if (hit.tipo === "texto") return;
            // Las matrículas siempre abren el diálogo (para ver la placa generada);
            // el resto, sólo si aún no están identificados.
            if (hit.conocido && hit.tipo !== "matricula") return;
            abrirDialogo(hit);
        });

        // Relleno inicial con lo más reciente y conexión en tiempo real.
        fetch("/api/directo/estado")
            .then(function (r) { return r.json(); })
            .then(function (estado) {
                (estado.recientes || []).slice(0, MAX_ITEMS).reverse().forEach(push);
            })
            .catch(function () { });

        var connection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/detecciones")
            .withAutomaticReconnect()
            .build();

        connection.on("deteccion", push);
        connection.on("deteccion", marcarDeteccion);

        // Al leer una matrícula nueva se abre el diálogo con la placa generada.
        connection.on("deteccion", function (hit) {
            if (hit.tipo !== "matricula" || hit.conocido) return;
            if (document.querySelector(".mon-overlay")) return; // no interrumpe otro diálogo
            abrirDialogo(hit);
        });

        connection.start().catch(function (err) {
            console.error("No se pudo abrir el canal en tiempo real", err);
        });
    })();
})();
