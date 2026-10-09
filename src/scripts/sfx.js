// Game sound effects, synthesised with Web Audio (no audio files to download).
// One switch in the header turns them all on or off.
import { load, save } from './storage.js';

const KEY = 'winisland:sound';
let enabled = load(KEY, true) !== false;
let ctx = null;
let noise = null;
let lastTick = 0;

export const soundOn = () => enabled;

export function setSound(on) {
  enabled = on;
  save(KEY, on);
  if (!on) ctx?.suspend();
  window.dispatchEvent(new CustomEvent('winisland:sound', { detail: on }));
}

// Browsers only allow audio after the visitor has interacted with the page.
function audio() {
  if (!enabled) return null;
  if (navigator.userActivation && !navigator.userActivation.hasBeenActive) return null;
  if (!ctx) {
    const AudioCtx = window.AudioContext || window.webkitAudioContext;
    if (!AudioCtx) return null;
    ctx = new AudioCtx();
  }
  if (ctx.state === 'suspended') ctx.resume();
  return ctx;
}

function tone(ac, { freq, to, start = 0, length = 0.15, volume = 0.12, type = 'triangle' }) {
  const t = ac.currentTime + start;
  const osc = ac.createOscillator();
  const gain = ac.createGain();
  osc.type = type;
  osc.frequency.setValueAtTime(freq, t);
  if (to) osc.frequency.exponentialRampToValueAtTime(to, t + length);
  gain.gain.setValueAtTime(0.0001, t);
  gain.gain.exponentialRampToValueAtTime(volume, t + 0.01);
  gain.gain.exponentialRampToValueAtTime(0.0001, t + length);
  osc.connect(gain).connect(ac.destination);
  osc.start(t);
  osc.stop(t + length + 0.02);
}

function hit(ac, { start = 0, length = 0.08, volume = 0.25, freq = 900 }) {
  if (!noise) {
    noise = ac.createBuffer(1, ac.sampleRate * 0.5, ac.sampleRate);
    const data = noise.getChannelData(0);
    for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
  }
  const t = ac.currentTime + start;
  const src = ac.createBufferSource();
  const filter = ac.createBiquadFilter();
  const gain = ac.createGain();
  src.buffer = noise;
  filter.type = 'bandpass';
  filter.frequency.value = freq;
  gain.gain.setValueAtTime(volume, t);
  gain.gain.exponentialRampToValueAtTime(0.0001, t + length);
  src.connect(filter).connect(gain).connect(ac.destination);
  src.start(t);
  src.stop(t + length + 0.02);
}

export const sfx = {
  /** Wheel peg tick. */
  tick() {
    const now = performance.now();
    if (now - lastTick < 40) return;
    lastTick = now;
    const ac = audio();
    if (ac) tone(ac, { freq: 1500, to: 600, length: 0.045, volume: 0.07 });
  },

  /** Short UI pop. */
  pop() {
    const ac = audio();
    if (ac) tone(ac, { freq: 420, to: 880, length: 0.09, volume: 0.1, type: 'sine' });
  },

  /** Countdown beep; `last` is the higher "go" beep. */
  beep(last = false) {
    const ac = audio();
    if (ac) tone(ac, { freq: last ? 1320 : 660, length: last ? 0.35 : 0.16, volume: 0.13, type: 'square' });
  },

  /** Drumroll that speeds up over `seconds`. */
  drumroll(seconds = 1.5) {
    const ac = audio();
    if (!ac) return;
    let t = 0;
    let gap = 0.09;
    while (t < seconds) {
      hit(ac, { start: t, length: 0.07, volume: 0.12 + (t / seconds) * 0.18, freq: 220 });
      t += gap;
      gap = Math.max(0.035, gap * 0.94);
    }
  },

  /** Winner fanfare. */
  win() {
    const ac = audio();
    if (!ac) return;
    hit(ac, { length: 0.4, volume: 0.35, freq: 140 });
    [523.25, 659.25, 783.99, 1046.5].forEach((freq, i) => tone(ac, { freq, start: 0.08 + i * 0.09, length: 0.3, volume: 0.11 }));
    [1318.5, 1568, 2093].forEach((freq, i) => tone(ac, { freq, start: 0.5 + i * 0.06, length: 0.25, volume: 0.05, type: 'sine' }));
  },

  /** Level up jingle. */
  levelUp() {
    const ac = audio();
    if (!ac) return;
    [392, 523.25, 659.25, 783.99, 1046.5].forEach((freq, i) =>
      tone(ac, { freq, start: i * 0.07, length: 0.18, volume: 0.08, type: 'square' }),
    );
  },

  /** Coin flick / card whoosh. */
  whoosh() {
    const ac = audio();
    if (!ac) return;
    hit(ac, { length: 0.35, volume: 0.18, freq: 1800 });
    tone(ac, { freq: 300, to: 1200, length: 0.3, volume: 0.04, type: 'sine' });
  },

  /** Metal coin landing. */
  clink() {
    const ac = audio();
    if (!ac) return;
    tone(ac, { freq: 2637, length: 0.5, volume: 0.07, type: 'sine' });
    tone(ac, { freq: 3520, start: 0.02, length: 0.35, volume: 0.04, type: 'sine' });
    hit(ac, { length: 0.05, volume: 0.2, freq: 4000 });
  },

  /** Dice shaking in a cup. */
  rattle(seconds = 0.6) {
    const ac = audio();
    if (!ac) return;
    for (let t = 0; t < seconds; t += 0.045 + Math.random() * 0.04) {
      hit(ac, { start: t, length: 0.03, volume: 0.12 + Math.random() * 0.1, freq: 1200 + Math.random() * 1400 });
    }
  },

  /** Something landing on the table. */
  thud() {
    const ac = audio();
    if (!ac) return;
    hit(ac, { length: 0.12, volume: 0.35, freq: 180 });
    tone(ac, { freq: 140, to: 60, length: 0.15, volume: 0.12, type: 'sine' });
  },

  /** Magic sparkle for reveals. */
  sparkle() {
    const ac = audio();
    if (!ac) return;
    [1568, 2093, 2637, 3136, 2637, 3520].forEach((freq, i) =>
      tone(ac, { freq, start: i * 0.06, length: 0.3, volume: 0.05, type: 'sine' }),
    );
  },

  /** Badge unlocked chime. */
  badge() {
    const ac = audio();
    if (!ac) return;
    tone(ac, { freq: 987.77, length: 0.25, volume: 0.1, type: 'sine' });
    tone(ac, { freq: 1318.5, start: 0.12, length: 0.4, volume: 0.1, type: 'sine' });
  },
};
