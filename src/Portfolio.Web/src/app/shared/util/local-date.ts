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

const WEEKDAY_ABBR = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const MONTH_ABBR = [
  'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
];

/**
 * Formats a plain `DateOnly` "YYYY-MM-DD" string (e.g. a transaction's
 * `tradeDate` or a dividend payment's `exDate`) as `"Fri 24 Jul"`. Built on
 * `fromDateOnlyString`, so it reads local date parts directly and never
 * round-trips through `toISOString()` — see this file's header comment.
 */
export function formatDateOnly(value: string): string {
  const date = fromDateOnlyString(value);
  return `${WEEKDAY_ABBR[date.getDay()]} ${date.getDate()} ${MONTH_ABBR[date.getMonth()]}`;
}

/**
 * Formats a D20 `priceAsOf` CLOSE timestamp — e.g. `"2026-07-24T00:00:00+00:00"`
 * — as `"Fri 24 Jul"`. Used only for `priceSource: "Close"`, never for a
 * genuinely live quote; a stale close must never be labelled as if it were
 * fresh (tracker.md's D20 design note).
 *
 * `priceAsOf` is NOT the same shape as `tradeDate` — it carries an explicit
 * UTC offset because it's a real `DateTimeOffset` on the wire, and the
 * backend sets it to UTC midnight of the close's own calendar date. That
 * makes it tempting to reach for `new Date(priceAsOf)` and read the result's
 * LOCAL getters, the way the rest of this file does for plain `DateOnly`
 * strings — but that is a DIFFERENT bug from D19a, not the same fix. D19a's
 * timestamps were LOCAL midnight misread as UTC, which shifted the calendar
 * day back a day at any POSITIVE UTC offset (this machine's own Asia/Singapore,
 * UTC+8, is exactly where it was caught). Here the timestamp genuinely IS UTC
 * midnight, so reading it with local getters is safe at a positive offset —
 * UTC midnight + 8h is still the same calendar day — but rolls back a day at
 * any NEGATIVE offset instead: the mirror image of D19a, wrong in the
 * opposite direction. Slicing the ISO string's own date portion sidesteps the
 * asymmetry entirely and stays correct at every offset, not only the one this
 * machine happens to run at.
 */
export function formatCloseDate(priceAsOf: string): string {
  return formatDateOnly(priceAsOf.slice(0, 10));
}
