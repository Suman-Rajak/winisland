// Badges players unlock by using the tools. `earned` runs in the browser against the saved player state.
import { tools } from './tools.js';

const has = (s, stat, n = 1) => (s.stats[stat] ?? 0) >= n;

export const badges = [
  { id: 'first-spin', icon: '🎡', name: 'First Spin', desc: 'Spin the wheel once', earned: (s) => has(s, 'spins') },
  { id: 'wheel-wizard', icon: '🌀', name: 'Wheel Wizard', desc: 'Spin the wheel 25 times', earned: (s) => has(s, 'spins', 25) },
  { id: 'mic-check', icon: '🎤', name: 'Mic Check', desc: 'Call your first tambola number', earned: (s) => has(s, 'calls') },
  { id: 'full-house', icon: '🏠', name: 'Full House Host', desc: 'Call all 90 numbers in a game', earned: (s) => has(s, 'fullHouses') },
  { id: 'room-host', icon: '🎟️', name: 'Room Host', desc: 'Create a tambola room', earned: (s) => has(s, 'rooms') },
  { id: 'ticket-master', icon: '🖨️', name: 'Ticket Master', desc: 'Print tambola tickets', earned: (s) => has(s, 'ticketPrints') },
  { id: 'lucky-draw', icon: '🎁', name: 'Lucky Draw', desc: 'Pick a giveaway winner', earned: (s) => has(s, 'picks') },
  { id: 'team-captain', icon: '🧢', name: 'Team Captain', desc: 'Split players into teams', earned: (s) => has(s, 'teams') },
  { id: 'santa-helper', icon: '🎅', name: "Santa's Helper", desc: 'Run a Secret Santa draw', earned: (s) => has(s, 'santas') },
  { id: 'heads-up', icon: '🪙', name: 'Heads Up', desc: 'Flip a coin', earned: (s) => has(s, 'flips') },
  { id: 'snake-eyes', icon: '🐍', name: 'Snake Eyes', desc: 'Roll double ones with two dice', earned: (s) => Boolean(s.flags.snakeEyes) },
  {
    id: 'island-hopper',
    icon: '🏝️',
    name: 'Island Hopper',
    desc: `Visit all ${tools.length} islands`,
    earned: (s) => tools.every((t) => s.visited.includes(t.id)),
  },
  { id: 'on-fire', icon: '🔥', name: 'On Fire', desc: 'Play 3 days in a row', earned: (s) => s.streak >= 3 },
  { id: 'night-owl', icon: '🦉', name: 'Night Owl', desc: 'Play after midnight', earned: (s) => s.nightOwl },
];
