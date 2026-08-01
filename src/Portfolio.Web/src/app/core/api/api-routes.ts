// Relative URLs only — the dev server proxies /api and /hubs to the API
// (see proxy.conf.json), and the production Nginx config (Phase 10) does the
// same. Never hardcode http://localhost:5100 in a service.
// Ids are C# `int` and cross the wire as JSON numbers (see core/api/models.ts)
// — these signatures used to read `(id: string)`, which contradicted that
// rule and only didn't show up as a bug because template interpolation
// silently stringifies whatever it's given. Fixed to `number` at the source
// rather than casting at each call site.
export const API_ROUTES = {
  assets: '/api/assets',
  asset: (id: number) => `/api/assets/${id}`,
  transactions: '/api/transactions',
  transaction: (id: number) => `/api/transactions/${id}`,
  pricesStatus: '/api/prices/status',
  pricesRefresh: '/api/prices/refresh',
} as const;

export const PRICES_HUB_URL = '/hubs/prices';
