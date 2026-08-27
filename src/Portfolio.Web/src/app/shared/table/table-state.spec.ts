import { signal } from '@angular/core';

import { ALL_ROWS, TableSort, compareSortValues, createTableState } from './table-state';

interface Row {
  id: number;
  name: string;
  amount: number | null;
  date: string;
}

function makeState(rows: Row[], defaultSort: TableSort<'name' | 'amount' | 'date'>, opts?: Partial<Parameters<typeof createTableState<Row, 'name' | 'amount' | 'date'>>[0]>) {
  const rowsSignal = signal<Row[]>(rows);
  const state = createTableState<Row, 'name' | 'amount' | 'date'>({
    rows: rowsSignal,
    columns: {
      name: (r) => r.name,
      amount: (r) => r.amount,
      date: (r) => r.date,
    },
    defaultSort,
    ...opts,
  });
  return { state, rowsSignal };
}

describe('compareSortValues', () => {
  it('sorts nulls last in ascending order', () => {
    expect(compareSortValues(null, 5, 'asc')).toBeGreaterThan(0);
    expect(compareSortValues(5, null, 'asc')).toBeLessThan(0);
  });

  it('sorts nulls last in descending order too — never coerced to 0', () => {
    expect(compareSortValues(null, 5, 'desc')).toBeGreaterThan(0);
    expect(compareSortValues(5, null, 'desc')).toBeLessThan(0);
    // A null must never sort as if it were a real zero, ahead of a negative number.
    expect(compareSortValues(null, -5, 'desc')).toBeGreaterThan(0);
  });

  it('two nulls compare equal', () => {
    expect(compareSortValues(null, null, 'asc')).toBe(0);
  });

  it('compares decimal(28,10)-scale numbers on their real magnitude, not a rounded display form', () => {
    // $0.0005326 vs $0.00050448 — both would round to "$0.00" under a naive
    // 2dp comparison, but the underlying values are meaningfully different.
    expect(compareSortValues(0.0005326, 0.00050448, 'asc')).toBeGreaterThan(0);
    expect(compareSortValues(0.00050448, 0.0005326, 'asc')).toBeLessThan(0);
  });

  it('compares date strings lexicographically (YYYY-MM-DD), matching chronological order', () => {
    expect(compareSortValues('2026-07-24', '2026-08-01', 'asc')).toBeLessThan(0);
    expect(compareSortValues('2026-08-01', '2026-07-24', 'desc')).toBeLessThan(0);
  });
});

describe('createTableState', () => {
  const ROWS: Row[] = [
    { id: 1, name: 'AAPL', amount: 100, date: '2026-07-20' },
    { id: 2, name: 'MSFT', amount: null, date: '2026-07-24' },
    { id: 3, name: 'ANVL', amount: 0.0005326, date: '2026-08-01' },
    { id: 4, name: 'GOOGL', amount: 50, date: '2026-08-01' },
  ];

  it('applies the default sort with no interaction', () => {
    const { state } = makeState(ROWS, { active: 'name', direction: 'asc' });
    expect(state.sorted().map((r) => r.name)).toEqual(['AAPL', 'ANVL', 'GOOGL', 'MSFT']);
  });

  it('sorts nulls last regardless of direction on a real column', () => {
    const { state } = makeState(ROWS, { active: 'amount', direction: 'asc' });
    expect(state.sorted().map((r) => r.name)).toEqual(['ANVL', 'GOOGL', 'AAPL', 'MSFT']);

    state.setSort({ active: 'amount', direction: 'desc' });
    expect(state.sorted().map((r) => r.name)).toEqual(['AAPL', 'GOOGL', 'ANVL', 'MSFT']);
  });

  it('applies a stable tiebreak when the primary column compares equal', () => {
    const { state } = makeState(ROWS, { active: 'date', direction: 'desc' }, { tiebreak: (a, b) => b.id - a.id });
    // ANVL and GOOGL share 2026-08-01 — tiebreak (higher id first) decides between them.
    expect(state.sorted().map((r) => r.name)).toEqual(['GOOGL', 'ANVL', 'MSFT', 'AAPL']);
  });

  it('paginates and resets to page 0 on a new sort', () => {
    const { state } = makeState(ROWS, { active: 'name', direction: 'asc' }, { pageSizeOptions: [2, 4], initialPageSize: 2 });
    expect(state.paged().map((r) => r.name)).toEqual(['AAPL', 'ANVL']);

    state.setPageIndex(1);
    expect(state.paged().map((r) => r.name)).toEqual(['GOOGL', 'MSFT']);

    state.setSort({ active: 'date', direction: 'asc' });
    expect(state.pageIndex()).toBe(0);
  });

  it('resets to page 0 on a page-size change', () => {
    const { state } = makeState(ROWS, { active: 'name', direction: 'asc' }, { pageSizeOptions: [2, 4], initialPageSize: 2 });
    state.setPageIndex(1);
    state.setPageSize(4);
    expect(state.pageIndex()).toBe(0);
  });

  it('ALL_ROWS puts every row on one page', () => {
    const { state } = makeState(ROWS, { active: 'name', direction: 'asc' }, { pageSizeOptions: [2, 4] });
    state.setPageSize(ALL_ROWS);
    expect(state.paged().length).toBe(4);
    expect(state.pageCount()).toBe(1);
  });

  it('clamps the page index — deleting the last row on the last page does not strand the user on an empty page', () => {
    const { state, rowsSignal } = makeState(ROWS, { active: 'name', direction: 'asc' }, { pageSizeOptions: [2, 4], initialPageSize: 2 });
    state.setPageIndex(1); // page 1: GOOGL, MSFT (last page, 2 rows)
    expect(state.paged().length).toBe(2);

    // Delete down to 3 rows — page 1 (2 rows/page) now only has 1 row: still valid.
    rowsSignal.set(ROWS.slice(0, 3));
    expect(state.pageIndex()).toBe(1);
    expect(state.paged().length).toBe(1);

    // Delete down to 2 rows — page 1 no longer exists (only page 0 does). Clamp, don't strand.
    rowsSignal.set(ROWS.slice(0, 2));
    expect(state.pageIndex()).toBe(0);
    expect(state.paged().length).toBe(2);

    // Delete everything — still a valid (empty) page 0, never a negative index.
    rowsSignal.set([]);
    expect(state.pageIndex()).toBe(0);
    expect(state.paged()).toEqual([]);
  });

  it('composes with an upstream filter — filtering, sorting and paging all apply together', () => {
    const rowsSignal = signal<Row[]>(ROWS);
    const filter = signal<'all' | 'priced'>('all');
    const filtered = () => (filter() === 'all' ? rowsSignal() : rowsSignal().filter((r) => r.amount !== null));

    // The table-state helper deliberately does not own filtering — it takes
    // whatever rows signal the caller hands it, so this simulates a page
    // component recomputing its own filtered signal and feeding it in.
    const filteredSignal = signal<Row[]>(filtered());
    const real = createTableState<Row, 'name' | 'amount' | 'date'>({
      rows: filteredSignal,
      columns: { name: (r) => r.name, amount: (r) => r.amount, date: (r) => r.date },
      defaultSort: { active: 'name', direction: 'asc' },
      pageSizeOptions: [2, 4],
      initialPageSize: 2,
    });

    expect(real.total()).toBe(4);

    filter.set('priced');
    filteredSignal.set(filtered());
    real.resetPage();
    expect(real.total()).toBe(3);
    expect(real.paged().map((r) => r.name)).toEqual(['AAPL', 'ANVL']);

    real.setPageIndex(1);
    expect(real.paged().map((r) => r.name)).toEqual(['GOOGL']);
  });
});
