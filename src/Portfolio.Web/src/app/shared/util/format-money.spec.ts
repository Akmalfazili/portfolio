import { formatMoney, getMoneyFormatter } from './format-money';

// The full rendering-behaviour matrix (sub-cent floor, price mode, null
// sentinel, thousands separators, etc.) is already pinned by
// `shared/pipes/money.pipe.spec.ts` through `MoneyPipe`, which is now a
// one-line delegation to this function — duplicating all 20 cases here would
// just be the same assertions twice. This spec carries the cases specific to
// `formatMoney` as a standalone function: a couple of representative
// behaviour checks (so the function is not only ever exercised indirectly
// through the pipe), plus the memoization identity guarantees that only this
// module can assert.
describe('formatMoney', () => {
  it('never floors a sub-cent ANVL-scale price to $0.00', () => {
    expect(formatMoney(0.0005326)).toBe('$0.0005326');
  });

  it('renders null/undefined/blank as an em dash rather than $0.00 or NaN', () => {
    expect(formatMoney(null)).toBe('—');
    expect(formatMoney(undefined)).toBe('—');
    expect(formatMoney('')).toBe('—');
  });

  it('renders a per-share rate at its own precision in price mode', () => {
    // Intl.NumberFormat separates the ISO code from the amount with a
    // non-breaking space (U+00A0) — see money.pipe.spec.ts's identical note.
    expect(formatMoney(0.103, 'SGD', 'price')).toBe('SGD 0.103');
  });
});

describe('getMoneyFormatter (the memo cache formatMoney is built on)', () => {
  it('returns the SAME Intl.NumberFormat instance for a repeated (currency, digits) pair', () => {
    const first = getMoneyFormatter('USD', 2);
    const second = getMoneyFormatter('USD', 2);
    expect(second).toBe(first);
  });

  it('returns a DIFFERENT instance across currencies, even with the same digit count', () => {
    const usd = getMoneyFormatter('USD', 2);
    const sgd = getMoneyFormatter('SGD', 2);
    expect(sgd).not.toBe(usd);
  });

  it('returns a DIFFERENT instance across digit counts, even for the same currency', () => {
    const total = getMoneyFormatter('USD', 2);
    const subCent = getMoneyFormatter('USD', 8);
    expect(subCent).not.toBe(total);
  });
});
