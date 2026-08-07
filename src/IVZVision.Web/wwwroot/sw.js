/* Service worker mínimo para que la aplicación sea instalable (Chrome/Edge/Safari).
   No cachea el vídeo ni la API: todo pasa directo a la red; solo los estáticos
   básicos quedan en caché para abrir la app al instante. */
const CACHE = "ivzvision-v1";
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
