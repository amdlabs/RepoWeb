/* Aviso emergente al reconocer una cara conocida.

   Se muestra en cualquier pantalla de la aplicación: una tarjeta que entra desde
   el lateral con la foto del rostro, el nombre y el rótulo «Rostro conocido», y
   que se retira sola pasados unos segundos. La misma persona no vuelve a avisar
   hasta pasado un rato, para que quien se quede delante de una cámara no llene
   la pantalla de tarjetas. */
(function () {
    "use strict";

    if (typeof signalR === "undefined") return;

    var SEGUNDOS_VISIBLE = 8;
    var SEGUNDOS_SILENCIO = 60;   // mismo nombre: no repetir el aviso antes de esto
    var MAXIMO_EN_PANTALLA = 3;

    var ultimos = {};             // nombre -> marca de tiempo del último aviso
    var pila = null;

    function contenedor() {
        if (pila && document.body.contains(pila)) return pila;
        pila = document.createElement("div");
        pila.className = "avisos-pila";
        pila.setAttribute("aria-live", "polite");
        document.body.appendChild(pila);
        return pila;
    }

    function retirar(tarjeta) {
        if (!tarjeta || tarjeta.dataset.saliendo === "1") return;
        tarjeta.dataset.saliendo = "1";
        tarjeta.classList.remove("entrando");
        tarjeta.classList.add("saliendo");
        setTimeout(function () {
            if (tarjeta.parentNode) tarjeta.parentNode.removeChild(tarjeta);
        }, 350);
    }

    function mostrar(d) {
        var caja = contenedor();

        // Si hay demasiadas, se va la más antigua para dejar sitio.
        while (caja.children.length >= MAXIMO_EN_PANTALLA) retirar(caja.firstChild);

        var tarjeta = document.createElement("div");
        tarjeta.className = "aviso aviso-rostro entrando";

        if (d.miniatura) {
            var foto = document.createElement("img");
            foto.className = "aviso-foto";
            foto.src = d.miniatura;
            foto.alt = d.etiqueta || "Rostro conocido";
            if (d.escena) foto.dataset.escena = d.escena;
            tarjeta.appendChild(foto);
        }

        var cuerpo = document.createElement("div");
        cuerpo.className = "aviso-cuerpo";

        var titulo = document.createElement("div");
        titulo.className = "aviso-titulo";
        titulo.textContent = "Rostro conocido";
        cuerpo.appendChild(titulo);

        var nombre = document.createElement("div");
        nombre.className = "aviso-nombre";
        nombre.textContent = d.etiqueta || "Sin nombre";
        cuerpo.appendChild(nombre);

        var pie = document.createElement("div");
        pie.className = "aviso-pie";
        pie.textContent = (d.camara || "") + (d.hora ? " · " + d.hora : "");
        cuerpo.appendChild(pie);

        if (d.autorizado === false) {
            var marca = document.createElement("span");
            marca.className = "badge badge-danger";
            marca.textContent = "no autorizado";
            cuerpo.appendChild(marca);
        }

        tarjeta.appendChild(cuerpo);

        var cerrar = document.createElement("button");
        cerrar.className = "aviso-cerrar";
        cerrar.type = "button";
        cerrar.title = "Cerrar";
        cerrar.textContent = "✕";
        cerrar.addEventListener("click", function () { retirar(tarjeta); });
        tarjeta.appendChild(cerrar);

        caja.appendChild(tarjeta);

        // La animación de entrada arranca en el fotograma siguiente al insertarla.
        requestAnimationFrame(function () { tarjeta.classList.add("visible"); });

        var temporizador = setTimeout(function () { retirar(tarjeta); }, SEGUNDOS_VISIBLE * 1000);

        // Con el ratón encima no se va: da tiempo a leerla o a ampliar la foto.
        tarjeta.addEventListener("mouseenter", function () { clearTimeout(temporizador); });
        tarjeta.addEventListener("mouseleave", function () {
            temporizador = setTimeout(function () { retirar(tarjeta); }, 2500);
        });
    }

    function procesar(d) {
        if (!d || d.tipo !== "rostro" || !d.conocido) return;

        var clave = (d.etiqueta || "?").toLowerCase();
        var ahora = Date.now();
        if (ultimos[clave] && ahora - ultimos[clave] < SEGUNDOS_SILENCIO * 1000) return;
        ultimos[clave] = ahora;

        mostrar(d);
    }

    var conexion = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/detecciones")
        .withAutomaticReconnect()
        .build();

    conexion.on("deteccion", procesar);
    conexion.start().catch(function () { /* sin tiempo real: la aplicación sigue igual */ });
})();
