# winisland.in

Free party games ("islands") built with Astro as a static site, styled as a neon game universe with XP, levels and badges. See `CLAUDE.md` for the plan and rules.

## Commands

| Command           | What it does                              |
| ----------------- | ----------------------------------------- |
| `npm install`     | Install dependencies (Node 22.12+)        |
| `npm run dev`     | Dev server at http://localhost:4321       |
| `npm run build`   | Build the site into `dist/`               |
| `npm run preview` | Serve `dist/` locally (tests offline/PWA) |

The service worker only registers in production builds, so use `npm run build && npm run preview` to test offline mode. The build downloads the fonts (Lilita One, Nunito) once and serves them from our own domain.

## Turning on ads (AdSense)

1. Get approved by AdSense (you'll need the About, Contact and Privacy Policy pages first).
2. Open `src/data/ads.js` and paste your publisher ID into `ADSENSE_CLIENT` (looks like `ca-pub-1234567890123456`).
   That alone loads the AdSense script, adds the site-verification tag and fills in `/ads.txt`. Turn on **Auto ads** in AdSense if you want Google to place ads for you.
3. Optional: create display ad units in AdSense and paste their slot IDs into `AD_SLOTS`:
   - `belowTool`: under every game (best viewability)
   - `inGuide`: inside the guide text
   - `sidebar`: tall sticky ad beside the guide, desktop only
   - `home`: home page, below the islands
4. In AdSense, turn on the Google consent message for visitors from the EEA, UK and Switzerland.

Until step 2, no ad code loads and the slots take no space. In `npm run dev` they show as dashed boxes so you can see where they go. Ads never go inside the game area.

## How tambola rooms work (no server)

A room is just a 6-letter code. A player's tickets are worked out from the room code plus their name
(`ticketsForName` in `src/scripts/tickets.js`), and printed ticket #n from the room code plus n. The host's
claim checker, the player's phone and the printed sheet all run the same code, so they always agree.
Don't change the ticket generator without bumping `VERSION` there, or existing rooms' tickets change.

Secret Santa works the same way: each reveal link carries its match after the `#`, which is never sent to a server.

## Layout

- `src/pages/`: one file per page. Tool pages use `src/layouts/ToolPage.astro` (header, game, ad, guide, sidebar).
- `src/components/`: header HUD, trophy room, island art (`Island.astro`), icons, ad slot.
- `src/data/`: `tools.js` (live and coming-soon islands), `badges.js`, `ads.js`.
- `src/scripts/`: shared browser code: `random.js` (fair Web Crypto randomness), `seeded.js` (repeatable randomness for room tickets), `tickets.js` (tambola tickets, rooms, prize checks), `ticket-view.js` (on-screen and printed tickets), `santa.js`, `player.js` (XP, levels, badges), `hud.js` (toasts, trophy room), `sfx.js` (sound effects), `confetti.js`, `storage.js`, `tambola-calls.js`.
- Private pages (`/tambola-room/`, `/secret-santa-generator/reveal/`) are `noindex` and left out of the sitemap.
- `public/sw.js`: offline support. The build fills in the file list (see `astro.config.mjs`).
- `public/_headers`: Cloudflare Pages cache and security headers.

## Adding a new tool

1. Add it to `tools` in `src/data/tools.js` (and remove it from `comingSoon`).
2. Add an icon in `ToolIcon.astro` and a prop in `Island.astro` for its `id`.
3. Create the page with `ToolPage` and call `award(...)` from `src/scripts/player.js` when someone plays.

## Deploying to Cloudflare Pages

- Framework preset: Astro
- Build command: `npm run build`
- Output directory: `dist`
- Node version comes from `.node-version`.
