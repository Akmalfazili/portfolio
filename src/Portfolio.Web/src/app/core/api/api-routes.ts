// Relative URLs only — the dev server proxies /api and /hubs to the API
// (see proxy.conf.json), and the production Nginx config (Phase 10) does the
// same. Never hardcode http://localhost:5100 in a service.
// Ids are C# `int` and cross the wire as JSON numbers (see core/api/models.ts)
// — these signatures used to read `(id: string)`, which contradicted that
// rule and only didn't show up as a bug because template interpolation
// silently stringifies whatever it's given. Fixed to `number` at the source
// rather than casting at each call site.
import { AssetClass } from './models';

export const API_ROUTES = {
  assets: '/api/assets',
  asset: (id: number) => `/api/assets/${id}`,
  assetPerformance: (id: number) => `/api/assets/${id}/performance`,
  transactions: '/api/transactions',
  transaction: (id: number) => `/api/transactions/${id}`,
  transactionsByAsset: (assetId: number) => `/api/transactions?assetId=${assetId}`,
  pricesStatus: '/api/prices/status',
  pricesRefresh: '/api/prices/refresh',
  // AssetClass is sent CAPITALISED — "Stock"/"Crypto". Route enum binding is
  // case-sensitive (D11); the AssetClass union type already only allows the
  // capitalised form, so this is enforced at the type level too.
  portfolioSummary: (assetClass: AssetClass) => `/api/portfolio/${assetClass}/summary`,
  portfolioAllocation: (assetClass: AssetClass) => `/api/portfolio/${assetClass}/allocation`,
  stockAnnualReturns: '/api/portfolio/stock/annual-returns',
} as const;

export const PRICES_HUB_URL = '/hubs/prices';
