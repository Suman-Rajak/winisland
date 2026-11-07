// Guides (articles). The /guides/ index, the "More guides" box and the footer read this list.
// Each guide's page lives at src/pages/guides/<slug>.astro and passes its slug to GuidePage.
// `tool` is the id of the related island in src/data/tools.js.
export const guides = [
  {
    slug: 'how-to-play-tambola',
    title: 'How to Play Tambola (Housie): Rules, Tickets and Prizes',
    short: 'How to play tambola',
    blurb: 'The complete rules: how tickets work, how numbers are called, every popular prize and how to check claims.',
    tool: 'tambola',
    topic: 'Tambola',
    published: '2026-11-08',
    minutes: 8,
  },
  {
    slug: 'tambola-number-calls',
    title: 'Tambola Number Calls 1 to 90: Full List of Housie Nicknames',
    short: 'Tambola calls 1–90',
    blurb: 'Every number from 1 to 90 with its traditional housie call, why the calls exist and how to make up your own.',
    tool: 'tambola',
    topic: 'Tambola',
    published: '2026-11-08',
    minutes: 7,
  },
  {
    slug: 'kitty-party-tambola-ideas',
    title: 'Kitty Party Tambola: 12 Fun Twists, Themes and Prize Ideas',
    short: 'Kitty party tambola ideas',
    blurb: 'Theme tickets, extra prize patterns, dress codes and small prize ideas to make your kitty party housie the best one yet.',
    tool: 'tickets',
    topic: 'Tambola',
    published: '2026-11-08',
    minutes: 7,
  },
  {
    slug: 'secret-santa-rules',
    title: 'Secret Santa Rules: How to Organise a Gift Exchange',
    short: 'Secret Santa rules',
    blurb: 'Step-by-step rules for office, family and friends: drawing names, budgets, wish lists, deadlines and the reveal.',
    tool: 'santa',
    topic: 'Secret Santa',
    published: '2026-11-08',
    minutes: 8,
  },
  {
    slug: 'secret-santa-gift-ideas',
    title: 'Secret Santa Gift Ideas by Budget (Under ₹500, ₹1000 and More)',
    short: 'Secret Santa gift ideas',
    blurb: 'Thoughtful gift ideas sorted by budget and by person, plus how to pick a gift for someone you barely know.',
    tool: 'santa',
    topic: 'Secret Santa',
    published: '2026-11-08',
    minutes: 8,
  },
  {
    slug: 'how-to-run-a-fair-giveaway',
    title: 'How to Run a Fair Giveaway on Instagram or YouTube',
    short: 'Running a fair giveaway',
    blurb: 'Plan the prize, write clear rules, collect entries, pick a winner openly and deliver the prize without drama.',
    tool: 'picker',
    topic: 'Giveaways',
    published: '2026-11-08',
    minutes: 9,
  },
];

// Which guide topic each tool page links to in its "Guides" box.
export const toolTopics = {
  tambola: 'Tambola',
  tickets: 'Tambola',
  santa: 'Secret Santa',
  picker: 'Giveaways',
};

export const guideHref = (slug) => `/guides/${slug}/`;
