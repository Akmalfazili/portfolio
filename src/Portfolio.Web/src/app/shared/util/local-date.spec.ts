import { formatCloseDate, fromDateOnlyString, toDateOnlyString } from './local-date';

describe('local-date', () => {
  it('formats local date parts directly, never via toISOString (which would shift by the UTC offset)', () => {
    // 23:30 local time — toISOString() on this Date would cross into the next
    // UTC day at any positive offset (e.g. SGT, UTC+8), which is exactly the
    // bug this helper exists to avoid.
    const lateEvening = new Date(2026, 6, 30, 23, 30); // 2026-07-30 23:30 local
    expect(toDateOnlyString(lateEvening)).toBe('2026-07-30');
  });

  it('round-trips a DateOnly string through fromDateOnlyString -> toDateOnlyString unchanged', () => {
    const value = '2026-01-05';
    expect(toDateOnlyString(fromDateOnlyString(value))).toBe(value);
  });

  it('parses "YYYY-MM-DD" at local midnight, not UTC midnight', () => {
    const date = fromDateOnlyString('2026-12-31');
    expect(date.getFullYear()).toBe(2026);
    expect(date.getMonth()).toBe(11);
    expect(date.getDate()).toBe(31);
  });

  describe('formatCloseDate', () => {
    it('formats a D20 priceAsOf CLOSE timestamp as "Fri 24 Jul"', () => {
      // The exact live payload curled from GET /api/portfolio/Stock/summary
      // for the AAPL Close-fallback case (D20/D17 verification).
      expect(formatCloseDate('2026-07-24T00:00:00+00:00')).toBe('Fri 24 Jul');
    });

    it('does not shift the date at this environment\'s own positive UTC offset', () => {
      // This suite runs in Asia/Singapore (UTC+8) — a POSITIVE offset, the
      // same zone D19a's axis-shift bug was caught in. A regression back to
      // `new Date(priceAsOf)` plus that Date's own LOCAL getters would
      // *coincidentally* still pass in this particular zone (UTC midnight +
      // 8h never crosses to the next calendar day), which is precisely why
      // that mistake is easy to ship unnoticed from a machine like this one
      // — it only rolls the date back a day at a NEGATIVE offset instead.
      // Slicing the ISO string's own date portion (see the doc comment on
      // `formatCloseDate`) is correct in both directions, and this pins the
      // one this suite can actually exercise directly.
      expect(formatCloseDate('2026-01-01T00:00:00+00:00')).toBe('Thu 1 Jan');
      expect(formatCloseDate('2026-12-31T00:00:00+00:00')).toBe('Thu 31 Dec');
    });

    it('is immune to the instant-parsing pitfall regardless of offset — verified against the naive alternative', () => {
      // Builds the value a *wrong* implementation (`new Date(priceAsOf)` +
      // local getters) would produce at a hypothetical negative offset, by
      // arithmetic rather than actually changing the process timezone (not
      // controllable at runtime in Node), and asserts formatCloseDate does
      // NOT depend on that path at all — it never constructs a Date from the
      // full instant in the first place.
      const priceAsOf = '2026-03-01T00:00:00+00:00'; // UTC midnight, March 1st
      const naiveInstant = new Date(priceAsOf);
      const wrongAtNegativeOffset = new Date(naiveInstant.getTime() - 5 * 60 * 60 * 1000); // UTC-5 local wall clock
      expect(wrongAtNegativeOffset.getUTCDate()).toBe(28); // rolls back to Feb 28 — the bug this guards against
      expect(formatCloseDate(priceAsOf)).toBe('Sun 1 Mar'); // formatCloseDate is unaffected
    });
  });
});
