/* Selección de grupos de rostros que sobrevive al cambio de página.

   Los grupos marcados se recuerdan en el navegador, de modo que se puede ir
   recorriendo las páginas marcando todas las fichas que son la misma persona y
   unificarlas de una vez. Al enviar, los grupos marcados en páginas que ahora no
   se ven viajan igualmente como campos ocultos. */
(function () {
    "use strict";

    var CLAVE = "cerbero.rostros.seleccion";

    var form = document.getElementById("formUnificar");
    if (!form) return;

    var contador = document.getElementById("grupoSeleccionCuenta");
    var botonUnificar = document.getElementById("btnUnificar");
    var botonLimpiar = document.getElementById("btnLimpiarSeleccion");

    function leer() {
        try {
            var crudo = sessionStorage.getItem(CLAVE);
            var lista = crudo ? JSON.parse(crudo) : [];
            return Array.isArray(lista) ? lista.filter(function (n) { return typeof n === "number"; }) : [];
        } catch (e) {
            // Ventana privada o almacenamiento bloqueado: se sigue sin memoria.
            return [];
        }
    }

    function guardar(lista) {
        try { sessionStorage.setItem(CLAVE, JSON.stringify(lista)); } catch (e) { /* sin memoria */ }
    }

    function limpiar() {
        try { sessionStorage.removeItem(CLAVE); } catch (e) { /* sin memoria */ }
    }

    var seleccion = leer();

    function casillas() {
        return Array.prototype.slice.call(document.querySelectorAll('input[name="grupos"][type="checkbox"]'));
    }

    /// Los grupos marcados en otras páginas viajan como campos ocultos del formulario.
    function sincronizarOcultos() {
        Array.prototype.slice.call(form.querySelectorAll('input[type="hidden"][name="grupos"]'))
            .forEach(function (i) { i.remove(); });

        var visibles = casillas().map(function (c) { return parseInt(c.value, 10); });

        seleccion.forEach(function (id) {
            if (visibles.indexOf(id) !== -1) return;
            var oculto = document.createElement("input");
            oculto.type = "hidden";
            oculto.name = "grupos";
            oculto.value = String(id);
            form.appendChild(oculto);
        });
    }

    function pintar() {
        var total = seleccion.length;
        var enOtrasPaginas = total - casillas().filter(function (c) { return c.checked; }).length;

        if (contador) {
            contador.textContent = total === 0
                ? "ningún grupo marcado"
                : total + (total === 1 ? " grupo marcado" : " grupos marcados")
                  + (enOtrasPaginas > 0 ? " (" + enOtrasPaginas + " en otras páginas)" : "");
        }

        if (botonUnificar) botonUnificar.disabled = total < 2;
        if (botonLimpiar) botonLimpiar.hidden = total === 0;

        casillas().forEach(function (c) {
            var ficha = c.closest(".grupo-rostro");
            if (ficha) ficha.classList.toggle("grupo-marcado", c.checked);
        });

        sincronizarOcultos();
    }

    // Estado inicial de esta página según lo recordado.
    casillas().forEach(function (c) {
        var id = parseInt(c.value, 10);
        c.checked = seleccion.indexOf(id) !== -1;

        c.addEventListener("change", function () {
            var pos = seleccion.indexOf(id);
            if (c.checked && pos === -1) seleccion.push(id);
            else if (!c.checked && pos !== -1) seleccion.splice(pos, 1);
            guardar(seleccion);
            pintar();
        });
    });

    if (botonLimpiar) {
        botonLimpiar.addEventListener("click", function () {
            seleccion = [];
            limpiar();
            casillas().forEach(function (c) { c.checked = false; });
            pintar();
        });
    }

    // Una vez unificados, la selección deja de tener sentido: esos grupos ya no existen.
    form.addEventListener("submit", limpiar);

    pintar();
})();
