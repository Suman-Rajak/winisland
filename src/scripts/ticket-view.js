// Draws tambola tickets: on screen (cream "paper" tickets you can tap) and for printing.
import { ticketNumbers } from './tickets.js';

const el = (tag, className, text) => {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
};

/**
 * On-screen ticket.
 * - `marked`: numbers to dab (a Set)
 * - `latest`: number to ring
 * - `onToggle(n)`: makes numbers tappable buttons
 */
export function ticketCard(ticket, { title = '', tag = '', marked = new Set(), latest, onToggle } = {}) {
  const card = el('div', 'tt');
  const head = el('div', 'tt-head');
  head.append(el('span', 'tt-title', title), el('span', 'tt-tag', tag));

  const grid = el('div', 'tt-grid');
  for (const row of ticket.rows) {
    for (const n of row) {
      if (!n) {
        const empty = el('span', 'tt-cell tt-empty');
        empty.setAttribute('aria-hidden', 'true');
        grid.append(empty);
        continue;
      }
      const cell = el(onToggle ? 'button' : 'span', 'tt-cell', n);
      cell.dataset.n = n;
      if (marked.has(n)) cell.classList.add('is-marked');
      if (n === latest) cell.classList.add('is-latest');
      if (onToggle) {
        cell.type = 'button';
        cell.setAttribute('aria-pressed', String(marked.has(n)));
        cell.addEventListener('click', () => onToggle(n, cell));
      }
      grid.append(cell);
    }
  }

  if (!onToggle) {
    // A short summary for screen readers instead of 27 grid cells.
    grid.setAttribute('aria-hidden', 'true');
    const nums = ticketNumbers(ticket);
    const hit = nums.filter((n) => marked.has(n));
    card.append(el('p', 'sr-only', `${title}: numbers ${nums.join(', ')}. ${hit.length} of 15 called.`));
  }

  card.append(head, grid, el('div', 'tt-foot', 'winisland.in'));
  return card;
}

/** Ticket for paper: plain black and white table. */
export function printTicket(ticket, { title = '', tag = '' } = {}) {
  const wrap = el('div', 'pt');
  const head = el('div', 'pt-head');
  head.append(el('span', 'pt-title', title), el('span', 'pt-tag', tag));
  const table = el('table', 'pt-grid');
  for (const row of ticket.rows) {
    const tr = el('tr');
    for (const n of row) tr.append(el('td', n ? '' : 'pt-empty', n ? String(n) : ''));
    table.append(tr);
  }
  wrap.append(head, table);
  return wrap;
}

/** Split printed tickets into A4 pages and open the print dialog. */
export function printTickets(items, perPage = 6) {
  const pages = [];
  for (let i = 0; i < items.length; i += perPage) {
    const page = el('div', `print-page per-${perPage}`);
    page.append(...items.slice(i, i + perPage));
    pages.push(page);
  }
  printPages(pages);
}

/** Print only the given nodes (everything else on the page is hidden while printing). */
export function printPages(pages) {
  let sheet = document.getElementById('print-sheet');
  if (!sheet) {
    sheet = el('div');
    sheet.id = 'print-sheet';
    document.body.append(sheet);
  }
  sheet.replaceChildren(...pages);
  window.print();
}
