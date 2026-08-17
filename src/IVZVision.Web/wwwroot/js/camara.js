/* Formulario de cámara: muestra los campos de red o de USB según el tipo elegido
   y gestiona el descubrimiento de canales de un DVR/NVR. */
(function () {
    "use strict";

    var vendor = document.getElementById("Camera_Vendor");
    if (!vendor) return;

    var net = document.getElementById("netFields");
    var usb = document.getElementById("usbFields");
    var isapiBtn = document.getElementById("btnProbarIsapi");
    var canalesBtn = document.getElementById("btnBuscarCanales");

    function refresh() {
        var isUsb = vendor.value === "3"; // CameraVendor.Usb
        net.hidden = isUsb;
        usb.hidden = !isUsb;
        if (isapiBtn) isapiBtn.hidden = isUsb;
        if (canalesBtn) canalesBtn.hidden = isUsb;
    }

    vendor.addEventListener("change", refresh);
    refresh();

    /* ---------- Descubrimiento de canales del DVR/NVR ---------- */

    function token() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : "";
    }

    // Misma recogida de campos que config.js (los ocultos no pisan lo ya recogido).
    function collect(prefix) {
        var result = {};
        document.querySelectorAll('[name^="' + prefix + '."]').forEach(function (el) {
            var key = el.name.substring(prefix.length + 1);
            if (el.type === "checkbox") result[key] = el.checked;
            else if (el.type === "number") result[key] = el.value === "" ? 0 : Number(el.value);
            else if (el.tagName === "SELECT") result[key] = /^-?\d+$/.test(el.value) ? Number(el.value) : el.value;
            else if (el.type === "hidden") { if (!(key in result)) result[key] = el.value; }
            else result[key] = el.value;
        });
        return result;
    }

    async function post(handler, payload) {
        var response = await fetch("?handler=" + handler, {
            method: "POST",
            headers: { "Content-Type": "application/json", "RequestVerificationToken": token() },
            body: JSON.stringify(payload)
        });
        if (!response.ok) throw new Error("HTTP " + response.status + ": recargue la página (Ctrl+F5) y pruebe de nuevo.");
        return await response.json();
    }

    var out = document.getElementById("outCanales");
    var lista = document.getElementById("listaCanales");
    var items = document.getElementById("canalesItems");
    var agregarBtn = document.getElementById("btnAgregarCanales");

    function show(ok, message) {
        out.hidden = false;
        out.className = "test-output " + (ok ? "ok" : "ko");
        out.textContent = message;
    }

    if (canalesBtn) {
        canalesBtn.addEventListener("click", async function () {
            canalesBtn.disabled = true;
            lista.hidden = true;
            show(true, "Consultando los canales del equipo…");

            try {
                var data = await post("buscarCanales", collect("Camera"));
                show(data.ok, data.mensaje);

                if (data.ok && data.canales && data.canales.length) {
                    items.innerHTML = "";
                    data.canales.forEach(function (c) {
                        var label = document.createElement("label");
                        label.className = "check";
                        label.style.display = "block";

                        var cb = document.createElement("input");
                        cb.type = "checkbox";
                        cb.checked = c.enLinea !== false; // los canales caídos, desmarcados
                        cb.dataset.canal = c.canal;
                        cb.dataset.nombre = c.nombre || "";

                        var estado = c.enLinea === true ? " (en línea)"
                                   : c.enLinea === false ? " (sin señal)"
                                   : "";

                        label.appendChild(cb);
                        label.appendChild(document.createTextNode(
                            " Canal " + c.canal + " · " + (c.nombre || "sin nombre") + estado));
                        items.appendChild(label);
                    });
                    lista.hidden = false;
                }
            } catch (err) {
                show(false, "No se pudo completar la consulta: " + err.message);
            } finally {
                canalesBtn.disabled = false;
            }
        });
    }

    if (agregarBtn) {
        agregarBtn.addEventListener("click", async function () {
            var seleccionados = Array.from(items.querySelectorAll("input:checked")).map(function (cb) {
                return { canal: Number(cb.dataset.canal), nombre: cb.dataset.nombre };
            });

            if (!seleccionados.length) {
                show(false, "Marque al menos un canal.");
                return;
            }

            agregarBtn.disabled = true;
            try {
                var data = await post("agregarCanales", { camera: collect("Camera"), canales: seleccionados });
                show(data.ok, data.mensaje);
                if (data.ok) setTimeout(function () { window.location.href = "/Configuracion/Camaras"; }, 1200);
            } catch (err) {
                show(false, "No se pudo completar el alta: " + err.message);
            } finally {
                agregarBtn.disabled = false;
            }
        });
    }
})();
