// Relative URLs only — the dev server proxies /api and /hubs to the API
// (see proxy.conf.json), and the production Nginx config (Phase 10) does the
// same. Never hardcode http://localhost:5100 in a service.
export const API_ROUTES = {
  assets: '/api/assets',
  asset: (id: string) => `/api/assets/${id}`,
  transactions: '/api/transactions',
  transaction: (id: string) => `/api/transactions/${id}`,
  pricesStatus: '/api/prices/status',
  pricesRefresh: '/api/prices/refresh',
} as const;

export const PRICES_HUB_URL = '/hubs/prices';
