/* Pruebas de conexión de la pantalla de configuración, sin recargar la página. */
(function () {
    "use strict";

    function token() {
        const input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : "";
    }

    /** Reúne los campos de un prefijo ("Database.Server" → { server: … }). */
    function collect(prefix) {
        const result = {};
        document.querySelectorAll('[name^="' + prefix + '."]').forEach(function (el) {
            const key = el.name.substring(prefix.length + 1);
            if (el.type === "checkbox") {
                result[key] = el.checked;
            } else if (el.type === "number") {
                result[key] = el.value === "" ? 0 : Number(el.value);
            } else if (el.tagName === "SELECT") {
                // Las listas de enumerados llevan el valor numérico: hay que enviarlo como número.
                result[key] = /^-?\d+$/.test(el.value) ? Number(el.value) : el.value;
            } else if (el.type !== "hidden") {
                result[key] = el.value;
            }
        });
        return result;
    }

    function show(box, ok, message, preview) {
        box.hidden = false;
        box.className = "test-output " + (ok ? "ok" : "ko");
        box.textContent = message;

        if (preview) {
            const img = document.createElement("img");
            img.src = preview;
            img.alt = "Vista previa";
            box.appendChild(img);
        }
    }

    async function post(handler, payload) {
        const response = await fetch("?handler=" + handler, {
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                "RequestVerificationToken": token()
            },
            body: JSON.stringify(payload)
        });

        if (!response.ok) throw new Error("HTTP " + response.status);
        return await response.json();
    }

    function wire(buttonId, outputId, handler, prefix) {
        const button = document.getElementById(buttonId);
        if (!button) return;

        const box = document.getElementById(outputId);

        button.addEventListener("click", async function () {
            const original = button.textContent;
            button.disabled = true;
            button.textContent = "Probando…";
            show(box, true, "Ejecutando la prueba…");

            try {
                const data = await post(handler, collect(prefix));
                show(box, data.ok, data.mensaje, data.vistaPrevia);
            } catch (err) {
                show(box, false, "No se pudo completar la prueba: " + err.message);
            } finally {
                button.disabled = false;
                button.textContent = original;
            }
        });
    }

    wire("btnProbarBd", "outBd", "probarBd", "Database");
    wire("btnVerificarModelos", "outModelos", "verificarModelos", "Models");
    wire("btnProbarRtsp", "outRtsp", "probarRtsp", "Camera");
    wire("btnProbarIsapi", "outIsapi", "probarIsapi", "Camera");
})();

/* Alterna los bloques de cámara IP y USB según el origen elegido, y busca
   dispositivos USB en el equipo donde corre la aplicación. */
(function () {
    "use strict";

    const source = document.getElementById("camSource");
    if (!source) return;

    const usbBlock = document.getElementById("bloqueUsb");
    const ipBlock = document.getElementById("bloqueIp");
    const isapiButton = document.getElementById("btnProbarIsapi");

    function applySource() {
        // El valor del enum llega como número: 0 = Ip, 1 = Usb.
        const isUsb = String(source.value) === "1";

        if (usbBlock) usbBlock.hidden = !isUsb;
        if (ipBlock) ipBlock.hidden = isUsb;
        if (isapiButton) isapiButton.hidden = isUsb;
    }

    source.addEventListener("change", applySource);
    applySource();

    // Elegir de la lista rellena índice y ruta del dispositivo.
    const picker = document.getElementById("usbPicker");
    if (picker) {
        picker.addEventListener("change", function () {
            const option = picker.selectedOptions[0];
            if (!option || !option.value) return;

            const index = document.querySelector('[name="Camera.DeviceIndex"]');
            const path = document.querySelector('[name="Camera.DevicePath"]');
            if (index) index.value = option.value;
            if (path) path.value = option.dataset.path || "";
        });
    }

    const search = document.getElementById("btnBuscarUsb");
    if (!search) return;

    search.addEventListener("click", async function () {
        const box = document.getElementById("outUsb");
        const original = search.textContent;

        search.disabled = true;
        search.textContent = "Buscando…";
        box.hidden = false;
        box.className = "test-output";
        box.textContent = "Explorando los dispositivos del equipo…";

        try {
            const token = document.querySelector('input[name="__RequestVerificationToken"]');
            const response = await fetch("?handler=buscarUsb", {
                method: "POST",
                headers: { "RequestVerificationToken": token ? token.value : "" }
            });

            const data = await response.json();
            box.className = "test-output " + (data.ok ? "ok" : "ko");
            box.textContent = data.mensaje;
        } catch (err) {
            box.className = "test-output ko";
            box.textContent = "No se pudo completar la búsqueda: " + err.message;
        } finally {
            search.disabled = false;
            search.textContent = original;
        }
    });
})();
