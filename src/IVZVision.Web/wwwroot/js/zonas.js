/* Editor visual de zonas de detección: se dibujan rectángulos sobre el fotograma
   real de la cámara y se elige qué se detecta dentro de cada uno. Las zonas se
   guardan en porcentajes del fotograma, así que valen para cualquier resolución. */
(function () {
    "use strict";

    var panel = document.getElementById("zonasPanel");
    if (!panel) return;

    var lienzo = document.getElementById("zonasLienzo");
    var imagen = document.getElementById("zonasImagen");
    var lista = document.getElementById("zonasLista");
    var campo = document.getElementById("zonasJson");
    var vacio = document.getElementById("zonasVacio");
    var btnRefrescar = document.getElementById("zonasRefrescar");
    var btnLimpiar = document.getElementById("zonasLimpiar");
    var btnTodo = document.getElementById("zonasTodo");

    var zonas = [];
    try { zonas = JSON.parse(campo.value || "[]"); } catch (e) { zonas = []; }

    var TIPOS = [
        { clave: "faces", texto: "Rostros" },
        { clave: "plates", texto: "Matrículas" },
        { clave: "objects", texto: "Objetos" },
        { clave: "texts", texto: "Textos" }
    ];

    /* ---------- Persistencia en el formulario ---------- */

    function guardar() {
        campo.value = JSON.stringify(zonas);
        pintar();
    }

    // Al escribir en un campo no se redibuja la lista: perdería el foco.
    function guardarSilencioso() { campo.value = JSON.stringify(zonas); }

    /* ---------- Dibujo de los rectángulos sobre la imagen ---------- */

    function pintar() {
        Array.prototype.slice.call(lienzo.querySelectorAll(".zona")).forEach(function (z) { z.remove(); });

        zonas.forEach(function (z, i) {
            var caja = document.createElement("div");
            caja.className = "zona";
            caja.style.left = z.xPercent + "%";
            caja.style.top = z.yPercent + "%";
            caja.style.width = z.widthPercent + "%";
            caja.style.height = z.heightPercent + "%";

            var etiqueta = document.createElement("span");
            etiqueta.className = "zona-nombre";
            etiqueta.textContent = (i + 1) + ". " + (z.name || "Zona");
            caja.appendChild(etiqueta);

            lienzo.appendChild(caja);
        });

        pintarLista();
        vacio.hidden = zonas.length > 0;
    }

    function pintarLista() {
        lista.innerHTML = "";

        zonas.forEach(function (z, i) {
            var fila = document.createElement("div");
            fila.className = "zona-fila";

            var numero = document.createElement("span");
            numero.textContent = (i + 1) + ".";

            var nombre = document.createElement("input");
            nombre.value = z.name || "Zona";
            nombre.className = "zona-input";
            nombre.addEventListener("input", function () { z.name = nombre.value; guardarSilencioso(); });

            var tipos = document.createElement("div");
            tipos.className = "zona-tipos";
            TIPOS.forEach(function (t) {
                var etq = document.createElement("label");
                etq.className = "check";
                etq.style.margin = "0";

                var cb = document.createElement("input");
                cb.type = "checkbox";
                cb.checked = z[t.clave] !== false;
                cb.addEventListener("change", function () { z[t.clave] = cb.checked; guardarSilencioso(); });

                etq.appendChild(cb);
                etq.appendChild(document.createTextNode(" " + t.texto));
                tipos.appendChild(etq);
            });

            var medidas = document.createElement("span");
            medidas.className = "hint";
            medidas.textContent = Math.round(z.widthPercent) + " x " + Math.round(z.heightPercent) + " %";

            var borrar = document.createElement("button");
            borrar.type = "button";
            borrar.className = "btn small danger";
            borrar.textContent = "Quitar";
            borrar.addEventListener("click", function () { zonas.splice(i, 1); guardar(); });

            fila.appendChild(numero);
            fila.appendChild(nombre);
            fila.appendChild(tipos);
            fila.appendChild(medidas);
            fila.appendChild(borrar);
            lista.appendChild(fila);
        });
    }

    /* ---------- Dibujar arrastrando ---------- */

    var dibujando = null;

    function porcentaje(evento) {
        var r = lienzo.getBoundingClientRect();
        var punto = evento.touches ? evento.touches[0] : evento;
        return {
            x: Math.min(100, Math.max(0, (punto.clientX - r.left) * 100 / r.width)),
            y: Math.min(100, Math.max(0, (punto.clientY - r.top) * 100 / r.height))
        };
    }

    function empezar(e) {
        if (e.target.closest(".zona")) return;   // no se dibuja encima de otra zona
        e.preventDefault();

        var p = porcentaje(e);
        dibujando = { x0: p.x, y0: p.y, caja: document.createElement("div"), rect: null };
        dibujando.caja.className = "zona zona-dibujando";
        lienzo.appendChild(dibujando.caja);
    }

    function mover(e) {
        if (!dibujando) return;
        e.preventDefault();

        var p = porcentaje(e);
        var x = Math.min(dibujando.x0, p.x), y = Math.min(dibujando.y0, p.y);
        var w = Math.abs(p.x - dibujando.x0), h = Math.abs(p.y - dibujando.y0);

        dibujando.caja.style.left = x + "%";
        dibujando.caja.style.top = y + "%";
        dibujando.caja.style.width = w + "%";
        dibujando.caja.style.height = h + "%";
        dibujando.rect = { x: x, y: y, w: w, h: h };
    }

    function terminar() {
        if (!dibujando) return;

        var r = dibujando.rect;
        dibujando.caja.remove();
        dibujando = null;

        // Un clic suelto no crea zona: hace falta arrastrar un rectángulo con tamaño.
        if (!r || r.w < 3 || r.h < 3) { pintar(); return; }

        zonas.push({
            name: "Zona " + (zonas.length + 1),
            xPercent: Math.round(r.x * 10) / 10,
            yPercent: Math.round(r.y * 10) / 10,
            widthPercent: Math.round(r.w * 10) / 10,
            heightPercent: Math.round(r.h * 10) / 10,
            faces: true, plates: true, objects: true, texts: true
        });
        guardar();
    }

    lienzo.addEventListener("mousedown", empezar);
    window.addEventListener("mousemove", mover);
    window.addEventListener("mouseup", terminar);
    lienzo.addEventListener("touchstart", empezar, { passive: false });
    window.addEventListener("touchmove", mover, { passive: false });
    window.addEventListener("touchend", terminar);

    /* ---------- Botones ---------- */

    function refrescarImagen() {
        var id = panel.dataset.camara;
        if (!id || id === "00000000-0000-0000-0000-000000000000") {
            imagen.alt = "Guarde la cámara para ver su imagen y poder dibujar sobre ella.";
            return;
        }
        imagen.src = "/stream/" + id + "/instantanea?t=" + Date.now();
    }

    if (btnRefrescar) btnRefrescar.addEventListener("click", refrescarImagen);

    if (btnLimpiar) btnLimpiar.addEventListener("click", function () {
        if (zonas.length && !confirm("¿Quitar todas las zonas? Se volverá a analizar el fotograma completo.")) return;
        zonas = [];
        guardar();
    });

    if (btnTodo) btnTodo.addEventListener("click", function () {
        zonas.push({
            name: "Todo el encuadre",
            xPercent: 0, yPercent: 0, widthPercent: 100, heightPercent: 100,
            faces: true, plates: true, objects: true, texts: true
        });
        guardar();
    });

    imagen.addEventListener("error", function () {
        imagen.alt = "Sin imagen: la cámara aún no ha enviado fotogramas.";
    });

    refrescarImagen();
    pintar();
})();
