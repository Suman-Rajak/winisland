// @ts-check
import { defineConfig, fontProviders } from 'astro/config';
import sitemap from '@astrojs/sitemap';
import { createHash } from 'node:crypto';
import { readdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Files in dist/ that the service worker should not precache.
const SKIP_PRECACHE = [/^sw\.js$/, /^sitemap.*\.xml$/, /^robots\.txt$/, /^ads\.txt$/, /^_headers$/, /^404\.html$/, /^og-image\.png$/];

async function listFiles(dir, base = dir) {
  const out = [];
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) out.push(...(await listFiles(full, base)));
    else out.push(path.relative(base, full).split(path.sep).join('/'));
  }
  return out;
}

/**
 * After the build, fill dist/sw.js with every page and asset so the tools
 * work offline once the site has been opened. The cache name changes
 * whenever any file changes, which makes browsers pick up new deploys.
 */
function precacheServiceWorker() {
  return {
    name: 'winisland:precache-sw',
    hooks: {
      'astro:build:done': async ({ dir, logger }) => {
        const root = fileURLToPath(dir);
        const files = (await listFiles(root)).filter((f) => !SKIP_PRECACHE.some((re) => re.test(f))).sort();
        const hash = createHash('sha256');
        for (const f of files) hash.update(f).update(await readFile(path.join(root, f)));
        const urls = files.map((f) => '/' + f.replace(/(^|\/)index\.html$/, '$1'));
        const swPath = path.join(root, 'sw.js');
        const sw = (await readFile(swPath, 'utf8'))
          .replace('self.__PRECACHE_VERSION__', JSON.stringify(hash.digest('hex').slice(0, 12)))
          .replace('self.__PRECACHE_URLS__', JSON.stringify(urls));
        await writeFile(swPath, sw);
        logger.info(`Service worker precaches ${urls.length} files`);
      },
    },
  };
}

export default defineConfig({
  site: 'https://winisland.in',
  integrations: [
    // Private pages (player rooms, Secret Santa reveals) stay out of search.
    sitemap({ filter: (page) => !/\.txt$|\/tambola-room\/|\/reveal\//.test(page) }),
    precacheServiceWorker(),
  ],
  // Downloaded at build time and served from our own domain (no Google Fonts request at runtime).
  fonts: [
    {
      provider: fontProviders.google(),
      name: 'Lilita One',
      cssVariable: '--font-display',
      weights: [400],
      subsets: ['latin'],
      fallbacks: ['Arial Black', 'Impact', 'sans-serif'],
    },
    {
      provider: fontProviders.google(),
      name: 'Nunito',
      cssVariable: '--font-body',
      weights: ['400 900'],
      styles: ['normal'],
      subsets: ['latin'],
      fallbacks: ['system-ui', 'Segoe UI', 'Roboto', 'sans-serif'],
    },
  ],
});
