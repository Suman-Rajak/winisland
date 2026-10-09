// Player progress: XP, levels, daily streak and badges. Stored only in this browser.
// Every change fires a `winisland:player` event; hud.js turns it into toasts and the XP bar.
import { load, save } from './storage.js';
import { badges } from '../data/badges.js';

const KEY = 'winisland:player';
export const XP_FOR = {
  visit: 20,
  spin: 10,
  call: 2,
  fullHouse: 50,
  room: 20,
  tickets: 10,
  pick: 15,
  teams: 15,
  santa: 20,
  flip: 5,
  roll: 5,
  daily: 25,
  badge: 30,
};
const RANKS = ['Rookie', 'Party Starter', 'Crowd Pleaser', 'Hype Hero', 'Island Captain', 'Island Legend'];
const STAT_FOR = {
  spin: 'spins',
  call: 'calls',
  fullHouse: 'fullHouses',
  room: 'rooms',
  tickets: 'ticketPrints',
  pick: 'picks',
  teams: 'teams',
  santa: 'santas',
  flip: 'flips',
  roll: 'rolls',
};

function fresh() {
  return {
    xp: 0,
    stats: {},
    flags: {},
    visited: [],
    badges: [],
    streak: 0,
    lastDay: '',
    nightOwl: false,
  };
}

export function getPlayer() {
  const base = fresh();
  const saved = load(KEY, null);
  if (!saved || typeof saved !== 'object') return base;
  return {
    ...base,
    ...saved,
    xp: Number.isFinite(saved.xp) ? saved.xp : 0,
    stats: { ...saved.stats },
    flags: { ...saved.flags },
    visited: Array.isArray(saved.visited) ? saved.visited : [],
    badges: Array.isArray(saved.badges) ? saved.badges : [],
  };
}

/** Level L starts at 50 × (L − 1)² XP. */
export function levelInfo(xp) {
  const level = Math.floor(Math.sqrt(xp / 50)) + 1;
  const floor = 50 * (level - 1) ** 2;
  const next = 50 * level ** 2;
  return { level, rank: RANKS[Math.min(level, RANKS.length) - 1], floor, next, progress: (xp - floor) / (next - floor) };
}

const dayKey = (d) => `${d.getFullYear()}-${d.getMonth() + 1}-${d.getDate()}`;

function update(change) {
  const player = getPlayer();
  const levelBefore = levelInfo(player.xp).level;
  const events = [];
  const now = new Date();
  let gained = 0;

  const today = dayKey(now);
  if (player.lastDay !== today) {
    const yesterday = new Date(now);
    yesterday.setDate(now.getDate() - 1);
    player.streak = player.lastDay === dayKey(yesterday) ? player.streak + 1 : 1;
    player.lastDay = today;
    gained += XP_FOR.daily;
    events.push({ type: 'daily', streak: player.streak, xp: XP_FOR.daily });
  }
  if (now.getHours() < 4) player.nightOwl = true;

  gained += change(player, events);

  for (const badge of badges) {
    if (!player.badges.includes(badge.id) && badge.earned(player)) {
      player.badges.push(badge.id);
      gained += XP_FOR.badge;
      events.push({ type: 'badge', badge });
    }
  }

  player.xp += gained;
  const after = levelInfo(player.xp);
  if (after.level > levelBefore) events.push({ type: 'level', ...after });

  save(KEY, player);
  window.dispatchEvent(new CustomEvent('winisland:player', { detail: { player, gained, events } }));
  return player;
}

/** First visit to each island earns XP. */
export function visit(island) {
  return update((player, events) => {
    if (!island || player.visited.includes(island)) return 0;
    player.visited.push(island);
    events.push({ type: 'island', xp: XP_FOR.visit });
    return XP_FOR.visit;
  });
}

/** Record a play (a key of STAT_FOR). `flags` marks one-off feats, e.g. { snakeEyes: true }. */
export function award(action, flags = {}) {
  return update((player) => {
    const stat = STAT_FOR[action];
    player.stats[stat] = (player.stats[stat] ?? 0) + 1;
    Object.assign(player.flags, flags);
    return XP_FOR[action];
  });
}
