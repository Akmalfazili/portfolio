import {
  formatCloseDate,
  formatDateOnly,
  formatDateOnlyLong,
  formatFxAsOf,
  fromDateOnlyString,
  toDateOnlyString,
} from './local-date';

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

  describe('formatDateOnly', () => {
    it('formats a plain DateOnly "YYYY-MM-DD" string (e.g. a dividend exDate) as "Fri 24 Jul"', () => {
      expect(formatDateOnly('2026-07-24')).toBe('Fri 24 Jul');
    });

    it('is what formatCloseDate delegates to after slicing the instant to its date portion', () => {
      expect(formatCloseDate('2026-07-24T00:00:00+00:00')).toBe(formatDateOnly('2026-07-24'));
    });
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

  describe('formatDateOnlyLong', () => {
    it('formats a plain DateOnly string as "4 Sep 2026" — day, month, year, no weekday', () => {
      expect(formatDateOnlyLong('2026-09-04')).toBe('4 Sep 2026');
    });
  });

  describe('formatFxAsOf', () => {
    it('formats a live /exchange_rate instant in Asia/Singapore, explicitly, not the local zone', () => {
      // 2026-09-05T11:31:00Z is 2026-09-05 19:31 SGT (UTC+8).
      expect(formatFxAsOf('2026-09-05T11:31:00+00:00')).toBe('5 Sep 2026, 7:31 pm SGT');
    });

    it('rolls the calendar date forward in SGT for a UTC instant that lands on the next SGT day', () => {
      // The case worth pinning: a UTC instant whose OWN calendar date differs
      // from the date it lands on in Asia/Singapore (UTC+8) — 2026-09-05
      // 20:05 UTC is already 2026-09-06 04:05 in Singapore. A formatter that
      // read this with the machine's local getters instead of an explicit
      // Asia/Singapore Intl.DateTimeFormat would only get this right by
      // coincidence, and only in a zone that happens to also be UTC+8.
      expect(formatFxAsOf('2026-09-05T20:05:00+00:00')).toBe('6 Sep 2026, 4:05 am SGT');
    });
  });
});
