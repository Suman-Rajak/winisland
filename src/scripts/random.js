// Fair randomness from the Web Crypto API (not Math.random).

/** Random integer in [0, max) with no modulo bias. */
export function randomInt(max) {
  if (!Number.isInteger(max) || max <= 0 || max > 2 ** 32) {
    throw new RangeError('max must be an integer from 1 to 2^32');
  }
  // Reject values from the incomplete final block so every result is equally likely.
  const limit = Math.floor(2 ** 32 / max) * max;
  const buf = new Uint32Array(1);
  do {
    crypto.getRandomValues(buf);
  } while (buf[0] >= limit);
  return buf[0] % max;
}

/** Random float in [0, 1). */
export function randomFloat() {
  return randomInt(2 ** 32) / 2 ** 32;
}

/** New array with the items in random order (Fisher–Yates). */
export function shuffle(items) {
  const a = [...items];
  for (let i = a.length - 1; i > 0; i--) {
    const j = randomInt(i + 1);
    [a[i], a[j]] = [a[j], a[i]];
  }
  return a;
}
