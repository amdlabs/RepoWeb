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
