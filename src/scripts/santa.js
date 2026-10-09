// Secret Santa: drawing names and the private reveal links.
import { shuffle } from './random.js';

/**
 * Who gives to whom. Returns recipients by giver index, or null if the rules make it impossible.
 * `blocked(g, r)` is true when giver g must not get recipient r.
 */
export function drawPairs(count, blocked) {
  const people = [...Array(count).keys()];
  const ok = (g, r) => g !== r && !blocked(g, r);

  // Shuffle until everyone has a valid match: every valid draw is equally likely.
  for (let attempt = 0; attempt < 5000; attempt++) {
    const pick = shuffle(people);
    if (pick.every((r, g) => ok(g, r))) return pick;
  }

  // Strict rules make lucky shuffles rare, so search instead (most restricted givers first).
  const options = people.map((g) => people.filter((r) => ok(g, r)));
  const order = shuffle(people).sort((a, b) => options[a].length - options[b].length);
  const result = Array(count).fill(-1);
  const taken = new Set();
  let steps = 0;
  const place = (i) => {
    if (i === count) return true;
    if (++steps > 200000) return false;
    const g = order[i];
    for (const r of shuffle(options[g])) {
      if (taken.has(r)) continue;
      taken.add(r);
      result[g] = r;
      if (place(i + 1)) return true;
      taken.delete(r);
    }
    return false;
  };
  return place(0) ? result : null;
}

// Reveal links carry the match in the URL fragment (after #), which browsers never send to a
// server. It's encoded so the organiser doesn't see names by accident.
export function encodeMatch(data) {
  let binary = '';
  for (const byte of new TextEncoder().encode(JSON.stringify(data))) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function decodeMatch(text) {
  try {
    const binary = atob(text.replace(/-/g, '+').replace(/_/g, '/'));
    const data = JSON.parse(new TextDecoder().decode(Uint8Array.from(binary, (c) => c.charCodeAt(0))));
    return data && typeof data.g === 'string' && typeof data.r === 'string' ? data : null;
  } catch {
    return null;
  }
}

export const revealLink = (origin, data) => `${origin}/secret-santa-generator/reveal/#${encodeMatch(data)}`;
