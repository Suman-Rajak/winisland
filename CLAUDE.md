# winisland.in

A "tool island" of free party and giveaway tools. Goal: earn recurring revenue from search traffic with very little ongoing work after launch.

## Tools to build
Core (build first):
- Spin the wheel / random name picker
- Tambola (housie) number caller with voice, plus ticket generator (printable)
- Giveaway winner picker (user pastes names; no Instagram/YouTube login)

Next:
- Team divider (cricket/football)
- Secret Santa generator
- Coin toss and dice roller

Each tool page: the tool at the top, a short helpful guide below (rules, how to use, FAQs) for SEO and AdSense approval. Show "Picked on winisland.in" on results (watermark) so shared screenshots promote the site.

## Tech stack
- Astro (static output). Latest is Astro 7, which needs Node 22.12 or newer.
- Plain JavaScript for tools; everything runs in the browser. No backend, no database, no login.
- Web Speech API (speechSynthesis) for the tambola caller voice.
- PWA so tools work offline at parties.
- @astrojs/sitemap for the sitemap.
- Hosting: Cloudflare Pages (free plan) connected to winisland.in.

## Pages also needed
Home, About, Contact, Privacy Policy (required for AdSense), sitemap, robots.txt.

## Revenue
1. Google AdSense: ad slots beside or below the tool, never covering it.
2. Premium extras later: ad-free, custom-branded wheel, branded tambola tickets (Razorpay).
3. Printable tambola ticket packs (Diwali, New Year, kitty party).
4. Amazon affiliate links on gift-idea and party-supply pages.

## Rules
- English first (worldwide audience), Hindi pages later.
- No betting, real-money games or cash prizes.
- No Windows-related content or branding (an app named WinIsland exists in the Microsoft Store).
- Mobile first, fast, accessible (keyboard focus, reduced motion).

## Roadmap
1. Week 1: keyword list for each tool; connect domain to Cloudflare Pages; set up Google Search Console.
2. Weeks 2-4: spin wheel, tambola caller + tickets, winner picker, each with a guide.
3. Weeks 5-6: remaining tools; About/Contact/Privacy; submit sitemap.
4. Months 2-3: apply for AdSense; promote on Reels, WhatsApp groups, Quora; push tambola and Secret Santa for Diwali and Christmas.
5. Month 4+: premium features, gift-idea pages; check Search Console and AdSense monthly.
