// Explorer Connect service worker: the app shell only. API and audio requests are
// never cached; they always go to the PC.
const SHELL = 'shell-v1';
const FILES = ['/', '/app/app.js', '/app/app.css', '/manifest.webmanifest',
  '/app/icon-192.png', '/app/icon-512.png', '/apple-touch-icon.png'];

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(SHELL).then((c) => c.addAll(FILES)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(caches.keys()
    .then((keys) => Promise.all(keys.filter((k) => k !== SHELL).map((k) => caches.delete(k))))
    .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (event) => {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET' || url.origin !== location.origin) return;
  if (url.pathname.startsWith('/api/')) return; // never cached
  const path = url.pathname === '/index.html' ? '/' : url.pathname;
  if (!FILES.includes(path)) return;
  // Network first, so an updated Explorer Native updates the app; the cache when the PC is away.
  event.respondWith(fetch(event.request).then((res) => {
    if (res.ok) {
      const copy = res.clone();
      caches.open(SHELL).then((c) => c.put(path, copy));
    }
    return res;
  }).catch(() => caches.match(path)));
});
