/* Service worker mínimo para que la aplicación sea instalable (Chrome/Edge/Safari).
   No cachea el vídeo ni la API: todo pasa directo a la red; solo los estáticos
   básicos quedan en caché para abrir la app al instante. */
const CACHE = "cerbero-v2";
const ESTATICOS = ["/css/site.css", "/manifest.webmanifest", "/iconos/icono-192.png", "/iconos/icono-512.png"];

self.addEventListener("install", (e) => {
    e.waitUntil(caches.open(CACHE).then((c) => c.addAll(ESTATICOS)).catch(() => null));
    self.skipWaiting();
});

self.addEventListener("activate", (e) => {
    e.waitUntil(
        caches.keys().then((keys) =>
            Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))));
    self.clients.claim();
});

self.addEventListener("fetch", (e) => {
    const url = new URL(e.request.url);

    // El vídeo, la API y las páginas van siempre a la red.
    if (e.request.method !== "GET") return;
    if (url.pathname.startsWith("/stream/") || url.pathname.startsWith("/api/") || url.pathname.startsWith("/hubs/")) return;

    // Navegación: red primero y, sin conexión, un aviso legible en vez del error del navegador.
    if (e.request.mode === "navigate") {
        e.respondWith(
            fetch(e.request).catch(() => new Response(
                "<!doctype html><meta charset='utf-8'><title>Cerbero Garage</title>" +
                "<body style='font-family:system-ui;background:#0b0e13;color:#e6e9ef;padding:40px'>" +
                "<h1>Sin conexión</h1><p>No se puede contactar con el servidor de Cerbero Garage. " +
                "Compruebe la red y vuelva a intentarlo.</p></body>",
                { headers: { "Content-Type": "text/html; charset=utf-8" } })));
        return;
    }

    // Estáticos: red primero con reserva de caché (para abrir la app sin conexión).
    if (url.pathname.startsWith("/css/") || url.pathname.startsWith("/js/") ||
        url.pathname.startsWith("/lib/") || url.pathname.startsWith("/iconos/") ||
        url.pathname === "/manifest.webmanifest") {
        e.respondWith(
            fetch(e.request)
                .then((res) => {
                    const copy = res.clone();
                    caches.open(CACHE).then((c) => c.put(e.request, copy)).catch(() => null);
                    return res;
                })
                .catch(() => caches.match(e.request)));
    }
});
