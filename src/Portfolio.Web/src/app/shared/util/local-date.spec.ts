import { fromDateOnlyString, toDateOnlyString } from './local-date';

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
});
