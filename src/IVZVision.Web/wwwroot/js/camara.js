/* Formulario de cámara: muestra los campos de red o de USB según el tipo elegido. */
(function () {
    "use strict";

    var vendor = document.getElementById("Camera_Vendor");
    if (!vendor) return;

    var net = document.getElementById("netFields");
    var usb = document.getElementById("usbFields");
    var isapiBtn = document.getElementById("btnProbarIsapi");

    function refresh() {
        var isUsb = vendor.value === "3"; // CameraVendor.Usb
        net.hidden = isUsb;
        usb.hidden = !isUsb;
        if (isapiBtn) isapiBtn.hidden = isUsb;
    }

    vendor.addEventListener("change", refresh);
    refresh();
})();
