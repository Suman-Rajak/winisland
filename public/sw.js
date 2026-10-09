// Offline support. The build fills in the version and file list
// (see precacheServiceWorker in astro.config.mjs).
const VERSION = self.__PRECACHE_VERSION__;
const PRECACHE = self.__PRECACHE_URLS__;
const CACHE = `winisland-${VERSION}`;
const NETWORK_TIMEOUT_MS = 4000;
// Servers may send "Vary: Origin", and module scripts are requested with an Origin header,
// which would stop cached copies from matching. Our files are versioned, so ignore Vary.
const MATCH = { ignoreVary: true };

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches
      .open(CACHE)
      .then((cache) => cache.addAll(PRECACHE))
      .then(() => self.skipWaiting()),
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) => Promise.all(keys.filter((k) => k.startsWith('winisland-') && k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()),
  );
});

async function cachedPage(url) {
  const cache = await caches.open(CACHE);
  const path = url.pathname.endsWith('/') ? url.pathname : url.pathname + '/';
  return (await cache.match(url.pathname, MATCH)) || (await cache.match(path, MATCH)) || cache.match('/', MATCH);
}

// Pages: try the network first so visitors get fresh content, fall back to
// the cached copy when offline or when the network is too slow.
async function handlePage(request) {
  const url = new URL(request.url);
  const network = fetch(request).then(async (response) => {
    if (response.ok) (await caches.open(CACHE)).put(request, response.clone());
    return response;
  });
  network.catch(() => {}); // a late failure after the timeout is not an error
  const timeout = new Promise((resolve) => setTimeout(resolve, NETWORK_TIMEOUT_MS));
  try {
    const response = await Promise.race([network, timeout]);
    if (response) return response;
  } catch {
    // Offline: use the cache below.
  }
  return (await cachedPage(url)) || network;
}

// Everything else: cache first. Built assets have content hashes in their names.
async function handleAsset(request) {
  const cached = await caches.match(request, MATCH);
  if (cached) return cached;
  const response = await fetch(request);
  if (response.ok && new URL(request.url).pathname.startsWith('/_astro/')) {
    (await caches.open(CACHE)).put(request, response.clone());
  }
  return response;
}

self.addEventListener('fetch', (event) => {
  const { request } = event;
  if (request.method !== 'GET' || new URL(request.url).origin !== self.location.origin) return;
  event.respondWith(request.mode === 'navigate' ? handlePage(request) : handleAsset(request));
});
