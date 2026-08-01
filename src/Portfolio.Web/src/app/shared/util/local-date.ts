/**
 * `tradeDate` is a C# `DateOnly`, serialised as a plain "YYYY-MM-DD" string —
 * never an ISO instant. Material's datepicker hands back a JS `Date` in local
 * time, and `Date.toISOString()` converts to UTC first, which shifts the
 * calendar date by the timezone offset — at SGT (UTC+8) a same-day evening
 * entry crosses midnight UTC and lands on the *next* day once encoded, and
 * the reverse (`new Date("YYYY-MM-DD")`, which parses as UTC midnight) can
 * land on the *previous* local day when decoded back for editing. Both
 * directions must read/write local date parts directly, never round-trip
 * through UTC.
 */

/** Date -> "YYYY-MM-DD" using the Date's own local year/month/day. */
export function toDateOnlyString(date: Date): string {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

/** "YYYY-MM-DD" -> a Date at local midnight, the inverse of toDateOnlyString. */
export function fromDateOnlyString(value: string): Date {
  const [year, month, day] = value.split('-').map(Number);
  return new Date(year, month - 1, day);
}

/** Local midnight for "today", the datepicker's `[max]` bound so a future
 * trade date is rejected in the UI, not just by the server's 400. */
export function todayDateOnly(): Date {
  const now = new Date();
  return new Date(now.getFullYear(), now.getMonth(), now.getDate());
}
