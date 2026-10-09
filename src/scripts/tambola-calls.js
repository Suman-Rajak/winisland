// Traditional tambola / housie calls. Only well-known, family-friendly ones;
// numbers without a call are read out as "2 and 3, 23".
export const NICKNAMES = {
  1: "Kelly's eye",
  2: 'One little duck',
  3: 'Cup of tea',
  4: 'Knock at the door',
  5: 'Man alive',
  6: 'Half a dozen',
  7: 'Lucky seven',
  8: 'Garden gate',
  9: "Doctor's orders",
  10: 'Big fat hen',
  11: 'Legs eleven',
  12: 'One dozen',
  13: 'Unlucky for some',
  14: "Valentine's Day",
  15: 'Young and keen',
  16: 'Sweet sixteen',
  17: 'Dancing queen',
  18: 'Coming of age',
  19: 'Goodbye teens',
  20: 'One score',
  21: 'Key of the door',
  22: 'Two little ducks',
  23: 'Thee and me',
  24: 'Two dozen',
  25: 'Duck and dive',
  26: 'Pick and mix',
  27: 'Gateway to heaven',
  28: 'In a state',
  29: 'Rise and shine',
  31: 'Get up and run',
  32: 'Buckle my shoe',
  33: 'All the threes',
  34: 'Ask for more',
  35: 'Jump and jive',
  36: 'Three dozen',
  38: 'Christmas cake',
  39: 'Thirty-nine steps',
  44: 'All the fours',
  45: 'Halfway there',
  46: 'Up to tricks',
  48: 'Four dozen',
  50: 'Half a century',
  52: 'Deck of cards',
  53: 'Stuck in the tree',
  55: 'All the fives',
  60: 'Five dozen',
  62: 'Tickety-boo',
  66: 'Clickety click',
  67: 'Stairway to heaven',
  71: 'Bang on the drum',
  72: 'Six dozen',
  73: 'Queen bee',
  76: 'Trombones',
  77: 'All the sevens',
  81: 'Stop and run',
  83: 'Time for tea',
  84: 'Seven dozen',
  85: 'Staying alive',
  86: 'Between the sticks',
  88: 'All the eights',
  89: 'Nearly there',
  90: 'Top of the house',
};

/** Plain call, e.g. "Single number 7" or "2 and 3, 23". */
export function plainCall(n) {
  if (n < 10) return `Single number ${n}`;
  const digits = String(n);
  return `${digits[0]} and ${digits[1]}, ${n}`;
}

/** Text to show and speak for a number. */
export function callFor(n, traditional) {
  const nickname = traditional ? NICKNAMES[n] : undefined;
  return nickname ? `${nickname}, ${n}` : plainCall(n);
}
