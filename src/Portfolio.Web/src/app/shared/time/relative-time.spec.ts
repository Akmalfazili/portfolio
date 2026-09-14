import { formatRelativeTime, isRefreshStale } from './relative-time';

describe('formatRelativeTime', () => {
  const now = new Date('2026-07-31T12:10:00Z').getTime();

  it('returns "never" for null', () => {
    expect(formatRelativeTime(null, now)).toBe('never');
  });

  it('returns "just now" for sub-5-second gaps', () => {
    expect(formatRelativeTime('2026-07-31T12:09:58Z', now)).toBe('just now');
  });

  it('formats seconds ago', () => {
    expect(formatRelativeTime('2026-07-31T12:09:30Z', now)).toBe('30s ago');
  });

  it('formats minutes ago', () => {
    expect(formatRelativeTime('2026-07-31T12:08:00Z', now)).toBe('2 min ago');
  });

  it('formats hours ago', () => {
    expect(formatRelativeTime('2026-07-31T09:10:00Z', now)).toBe('3h ago');
  });

  it('formats days ago', () => {
    expect(formatRelativeTime('2026-07-28T12:10:00Z', now)).toBe('3d ago');
  });
});

describe('isRefreshStale', () => {
  const now = new Date('2026-07-31T12:00:00Z').getTime();

  it('is stale when nothing has ever refreshed', () => {
    expect(isRefreshStale(null, now)).toBe(true);
  });

  it('is not stale when within grace of the scheduled next run', () => {
    const status = {
      lastRefreshedAt: '2026-07-31T11:55:00Z',
      nextScheduledRunAt: '2026-07-31T12:55:00Z', // 60-min closed-market cadence
    };
    expect(isRefreshStale(status, now)).toBe(false);
  });

  it('is stale once past the scheduled next run plus grace', () => {
    const status = {
      lastRefreshedAt: '2026-07-31T10:00:00Z',
      nextScheduledRunAt: '2026-07-31T11:00:00Z',
    };
    // now is 12:00, next due 11:00 + 5min grace = 11:05 — well past it.
    expect(isRefreshStale(status, now)).toBe(true);
  });

  it('falls back to a fixed window when nextScheduledRunAt is unknown', () => {
    const recentStatus = { lastRefreshedAt: '2026-07-31T11:50:00Z', nextScheduledRunAt: null };
    const staleStatus = { lastRefreshedAt: '2026-07-31T11:00:00Z', nextScheduledRunAt: null };
    expect(isRefreshStale(recentStatus, now)).toBe(false);
    expect(isRefreshStale(staleStatus, now)).toBe(true);
  });
});
