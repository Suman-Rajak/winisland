// Tambola tickets and rooms.
//
// A ticket has 3 rows and 9 columns. Each row holds 5 numbers, each column 1 to 3 numbers,
// and column c holds numbers from its range (1–9, 10–19, … 80–90) in ascending order.
// Tickets are made in "sheets" of 6 that together use every number from 1 to 90 once.
//
// Rooms need no server: a ticket is worked out from the room code plus the player's name
// (or the ticket number), so the host and the player always get the same ticket.
// Changing the generator changes everyone's tickets, so bump VERSION if you ever do.
import { randomInt } from './random.js';
import { seededRandom, seededShuffle } from './seeded.js';

const VERSION = 'v1';
const COLUMNS = Array.from({ length: 9 }, (_, c) => {
  const from = c === 0 ? 1 : c * 10;
  const to = c === 8 ? 90 : c * 10 + 9;
  return Array.from({ length: to - from + 1 }, (_, i) => from + i);
});

/** How many numbers each of the 6 tickets gets in each column (1–3, 15 per ticket). */
function sheetCounts(rng) {
  for (;;) {
    const counts = Array.from({ length: 6 }, () => Array(9).fill(1));
    const need = Array(6).fill(6); // every ticket needs 6 numbers on top of one per column
    let ok = true;
    for (const c of seededShuffle([...COLUMNS.keys()], rng)) {
      for (let extra = COLUMNS[c].length - 6; extra > 0; extra--) {
        const open = [0, 1, 2, 3, 4, 5].filter((t) => need[t] > 0 && counts[t][c] < 3);
        if (!open.length) {
          ok = false;
          break;
        }
        const most = Math.max(...open.map((t) => need[t]));
        const best = open.filter((t) => need[t] === most);
        const t = best[Math.floor(rng() * best.length)];
        counts[t][c] += 1;
        need[t] -= 1;
      }
      if (!ok) break;
    }
    if (ok) return counts;
  }
}

/** Choose which rows get a number in each column so every row has exactly 5. */
function rowLayout(colCounts, rng) {
  for (;;) {
    const perRow = [0, 0, 0];
    const filled = [[], [], []];
    const order = seededShuffle([...colCounts.keys()], rng).sort((a, b) => colCounts[b] - colCounts[a]);
    let ok = true;
    for (const c of order) {
      const open = seededShuffle([0, 1, 2], rng)
        .filter((r) => perRow[r] < 5)
        .sort((a, b) => perRow[a] - perRow[b]);
      if (open.length < colCounts[c]) {
        ok = false;
        break;
      }
      for (const r of open.slice(0, colCounts[c])) {
        filled[r][c] = true;
        perRow[r] += 1;
      }
    }
    if (ok && perRow.every((n) => n === 5)) return filled;
  }
}

/** Six tickets that together use 1–90 once. Each ticket is { rows: number|null[3][9] }. */
export function makeSheet(seed) {
  const rng = seededRandom(seed);
  const counts = sheetCounts(rng);
  const pools = COLUMNS.map((nums) => seededShuffle(nums, rng));
  return counts.map((colCounts) => {
    const layout = rowLayout(colCounts, rng);
    const rows = [Array(9).fill(null), Array(9).fill(null), Array(9).fill(null)];
    colCounts.forEach((k, c) => {
      const nums = pools[c].splice(0, k).sort((a, b) => a - b);
      [0, 1, 2].filter((r) => layout[r][c]).forEach((r, i) => (rows[r][c] = nums[i]));
    });
    return { rows };
  });
}

export const ticketNumbers = (ticket) => ticket.rows.flat().filter(Boolean);

// --- Rooms ---
const CODE_CHARS = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789'; // no 0/O, 1/I/L

export function newRoomCode() {
  return Array.from({ length: 6 }, () => CODE_CHARS[randomInt(CODE_CHARS.length)]).join('');
}

export function cleanRoomCode(text) {
  const code = String(text ?? '')
    .toUpperCase()
    .replace(/[^A-Z0-9]/g, '');
  return /^[A-Z2-9]{6}$/.test(code) ? code : '';
}

export const cleanPerPlayer = (value) => Math.min(6, Math.max(1, Math.round(Number(value)) || 1));

export const normalizeName = (name) => String(name).trim().replace(/\s+/g, ' ').toLowerCase();

/** A player's tickets: the first `count` tickets of their own sheet. */
export function ticketsForName(room, name, count) {
  return makeSheet(`${VERSION}|${room}|name:${normalizeName(name)}`).slice(0, count);
}

/** Printed ticket number n (1, 2, 3 …). Tickets 1–6, 7–12 and so on each form a full sheet. */
export function ticketByNumber(room, n) {
  return makeSheet(`${VERSION}|${room}|sheet:${Math.floor((n - 1) / 6)}`)[(n - 1) % 6];
}

/** "7" or "#7" means ticket number 7; anything else is a player name. */
export function parseClaim(text) {
  const match = String(text).trim().match(/^#?\s*(\d{1,4})$/);
  return match ? { number: Number(match[1]) } : { name: String(text).trim() };
}

export function inviteLink(origin, room, perPlayer) {
  return `${origin}/tambola-room/?r=${room}&k=${perPlayer}`;
}

// --- Prizes ---

/**
 * Prize progress for a ticket. `called` is the list of numbers in the order they were called.
 * Each prize has `at` (the call, 1-based, that completed it, or null), `left` (numbers still needed)
 * and `missing` (the uncalled numbers it needs; empty for early five, where any number counts).
 * Only called numbers count, never what a player has marked.
 */
export function prizes(ticket, called) {
  const when = new Map(called.map((n, i) => [n, i + 1]));
  const pattern = (id, name, nums) => {
    const missing = nums.filter((n) => !when.has(n)).sort((a, b) => a - b);
    const at = missing.length ? null : Math.max(...nums.map((n) => when.get(n)));
    return { id, name, left: missing.length, missing, at };
  };
  const rows = ticket.rows.map((row) => row.filter(Boolean));
  const hits = ticketNumbers(ticket)
    .filter((n) => when.has(n))
    .map((n) => when.get(n))
    .sort((a, b) => a - b);
  return [
    {
      id: 'early',
      name: 'Early five',
      left: Math.max(0, 5 - hits.length),
      missing: [],
      at: hits.length >= 5 ? hits[4] : null,
    },
    pattern('top', 'Top line', rows[0]),
    pattern('middle', 'Middle line', rows[1]),
    pattern('bottom', 'Bottom line', rows[2]),
    pattern('corners', 'Four corners', [rows[0][0], rows[0][4], rows[2][0], rows[2][4]]),
    pattern('full', 'Full house', ticketNumbers(ticket)),
  ];
}
