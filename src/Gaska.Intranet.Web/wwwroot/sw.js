// Service worker of the installed app (PWA). The intranet works only online (Blazor Server, live data), so nothing
// is cached except a small "no connection" page shown when opening a page fails. API calls, Blazor's connection
// and files are never intercepted.
const OFFLINE_CACHE = "gaska-offline-v1";
const OFFLINE_URL = "/offline.html";

self.addEventListener("install", event => {
    event.waitUntil(caches.open(OFFLINE_CACHE).then(cache => cache.add(new Request(OFFLINE_URL, { cache: "reload" }))));
    self.skipWaiting();
});

self.addEventListener("activate", event => {
    event.waitUntil(caches.keys()
        .then(keys => Promise.all(keys.filter(key => key !== OFFLINE_CACHE).map(key => caches.delete(key))))
        .then(() => self.clients.claim()));
});

self.addEventListener("fetch", event => {
    if (event.request.mode !== "navigate") {
        return;
    }

    event.respondWith(fetch(event.request).catch(() => caches.match(OFFLINE_URL)));
});
