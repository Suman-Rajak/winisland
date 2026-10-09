// Header sound switch.
import { setSound, sfx, soundOn } from './sfx.js';

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
