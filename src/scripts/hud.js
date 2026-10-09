// Header HUD: level, XP bar, badge count, trophy room, sound switch and toasts.
import { getPlayer, levelInfo } from './player.js';
import { setSound, sfx, soundOn } from './sfx.js';

const all = (selector) => document.querySelectorAll(selector);
const setText = (selector, value) => all(selector).forEach((el) => (el.textContent = value));

function renderPlayer(player) {
  const info = levelInfo(player.xp);
  setText('[data-hud-level]', info.level);
  setText('[data-hud-rank]', info.rank);
  setText('[data-hud-xp]', player.xp.toLocaleString());
  setText('[data-hud-next]', (info.next - player.xp).toLocaleString());
  setText('[data-hud-badges]', player.badges.length);
  all('[data-hud-fill]').forEach((el) => (el.style.transform = `scaleX(${Math.max(0.02, info.progress)})`));
  all('[data-badge]').forEach((el) => {
    const earned = player.badges.includes(el.dataset.badge);
    el.classList.toggle('earned', earned);
    const state = el.querySelector('.b-state');
    if (state) state.textContent = earned ? 'Unlocked' : 'Locked';
  });
  document.documentElement.dataset.player = 'ready';
}

// --- Toasts ---
const toasts = document.getElementById('toasts');

function toast({ icon, title, text, tone = 'violet' }) {
  if (!toasts) return;
  while (toasts.children.length >= 3) toasts.firstElementChild.remove();
  const el = document.createElement('div');
  el.className = `toast toast-${tone}`;
  const iconEl = Object.assign(document.createElement('span'), { className: 'toast-icon', textContent: icon });
  iconEl.setAttribute('aria-hidden', 'true');
  const body = document.createElement('span');
  body.append(
    Object.assign(document.createElement('strong'), { textContent: title }),
    Object.assign(document.createElement('span'), { textContent: text }),
  );
  el.append(iconEl, body);
  toasts.append(el);
  // Re-open as a popover so toasts sit above an open winner dialog too.
  if (toasts.showPopover) {
    if (toasts.matches(':popover-open')) toasts.hidePopover();
    toasts.showPopover();
  }
  setTimeout(() => {
    el.classList.add('leaving');
    setTimeout(() => el.remove(), 400);
  }, 3400);
}

function floatXP(amount) {
  const chip = document.querySelector('.level-chip');
  if (!chip) return;
  const pop = Object.assign(document.createElement('span'), { className: 'xp-pop', textContent: `+${amount} XP` });
  pop.setAttribute('aria-hidden', 'true');
  chip.append(pop);
  setTimeout(() => pop.remove(), 1200);
}

window.addEventListener('winisland:player', (event) => {
  const { player, gained, events } = event.detail;
  renderPlayer(player);
  if (gained > 0) floatXP(gained);
  for (const e of events) {
    if (e.type === 'island') toast({ icon: '🏝️', title: 'New island discovered!', text: `+${e.xp} XP` });
    if (e.type === 'daily') {
      toast({
        icon: '🔥',
        title: e.streak > 1 ? `Day ${e.streak} streak!` : 'Daily bonus',
        text: `+${e.xp} XP for playing today`,
        tone: 'pink',
      });
    }
    if (e.type === 'badge') {
      toast({ icon: e.badge.icon, title: `Badge unlocked: ${e.badge.name}`, text: e.badge.desc, tone: 'gold' });
      sfx.badge();
    }
    if (e.type === 'level') {
      toast({ icon: '⭐', title: `Level up! You're level ${e.level}`, text: `New rank: ${e.rank}`, tone: 'cyan' });
      sfx.levelUp();
    }
  }
});

// --- Trophy room ---
const room = document.getElementById('trophy-room');
all('[data-open-trophies]').forEach((button) =>
  button.addEventListener('click', () => {
    renderPlayer(getPlayer());
    room.showModal();
  }),
);
room?.addEventListener('click', (event) => {
  // Clicking the dark backdrop closes it.
  const box = room.getBoundingClientRect();
  const inside = event.clientX >= box.left && event.clientX <= box.right && event.clientY >= box.top && event.clientY <= box.bottom;
  if (!inside) room.close();
});

// --- Sound switch ---
const soundButton = document.getElementById('hud-sound');

function renderSound(on) {
  if (!soundButton) return;
  soundButton.setAttribute('aria-pressed', String(on));
  soundButton.title = on ? 'Sound effects on' : 'Sound effects off';
}

soundButton?.addEventListener('click', () => {
  setSound(!soundOn());
  sfx.pop();
});
window.addEventListener('winisland:sound', (event) => renderSound(event.detail));

renderSound(soundOn());
renderPlayer(getPlayer());
