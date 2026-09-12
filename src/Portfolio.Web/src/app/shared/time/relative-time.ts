/** Formats an ISO instant as a short "N min ago" style label, relative to `nowMs`. */
export function formatRelativeTime(fromIso: string | null, nowMs: number): string {
  if (!fromIso) {
    return 'never';
  }
  const from = new Date(fromIso).getTime();
  if (Number.isNaN(from)) {
    return 'never';
  }

  const diffSec = Math.round(Math.max(0, nowMs - from) / 1000);
  if (diffSec < 5) {
    return 'just now';
  }
  if (diffSec < 60) {
    return `${diffSec}s ago`;
  }
  const diffMin = Math.round(diffSec / 60);
  if (diffMin < 60) {
    return `${diffMin} min ago`;
  }
  const diffHour = Math.round(diffMin / 60);
  if (diffHour < 24) {
    return `${diffHour}h ago`;
  }
  const diffDay = Math.round(diffHour / 24);
  return `${diffDay}d ago`;
}

/**
 * Formats a still-pending future ISO instant as an "in N min"-style label,
 * relative to `nowMs` — the symmetric counterpart to `formatRelativeTime` for
 * a due time that has not happened yet (the refresh-details panel's "next
 * automatic check" row). A due time at or before `nowMs` reads as "due now"
 * rather than a confusing negative offset.
 */
export function formatDueIn(dueIso: string | null, nowMs: number): string {
  if (!dueIso) {
    return 'not yet scheduled';
  }
  const due = new Date(dueIso).getTime();
  if (Number.isNaN(due)) {
    return 'not yet scheduled';
  }

  const diffMs = due - nowMs;
  if (diffMs <= 0) {
    return 'due now';
  }
  const diffSec = Math.round(diffMs / 1000);
  if (diffSec < 60) {
    return `in ${diffSec}s`;
  }
  const diffMin = Math.round(diffSec / 60);
  if (diffMin < 60) {
    return `in ${diffMin} min`;
  }
  const diffHour = Math.round(diffMin / 60);
  if (diffHour < 24) {
    return `in ${diffHour}h`;
  }
  const diffDay = Math.round(diffHour / 24);
  return `in ${diffDay}d`;
}

/**
 * Whether the last refresh should be treated as stale for the amber warning
 * state. Prefers the backend's own `nextScheduledRunAt` (so a normal 60-minute
 * closed-market cadence is never mistaken for staleness) with a grace window
 * past it, and falls back to a fixed window when that isn't known yet (e.g.
 * before the first `RefreshStatus` snapshot arrives).
 */
export function isRefreshStale(
  status: { lastRefreshedAt: string | null; nextScheduledRunAt: string | null } | null,
  nowMs: number,
  graceMs = 5 * 60_000,
  fallbackWindowMs = 15 * 60_000,
): boolean {
  if (!status?.lastRefreshedAt) {
    return true;
  }

  if (status.nextScheduledRunAt) {
    const nextDue = new Date(status.nextScheduledRunAt).getTime();
    if (!Number.isNaN(nextDue)) {
      return nowMs > nextDue + graceMs;
    }
  }

  const last = new Date(status.lastRefreshedAt).getTime();
  return Number.isNaN(last) || nowMs - last > fallbackWindowMs;
}
