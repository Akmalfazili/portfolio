import { Routes } from '@angular/router';

// The Stocks and Crypto sections share PortfolioOverviewPage / AssetDetailPage,
// parameterised by `data.assetClass` — never fork these into duplicate
// per-section components. TransactionsPage covers both classes at once with
// its own in-page filter.
export const routes: Routes = [
  { path: '', redirectTo: 'stocks', pathMatch: 'full' },
  {
    path: 'stocks',
    data: { assetClass: 'Stock' },
    loadComponent: () =>
      import('./features/portfolio/portfolio-overview.page').then((m) => m.PortfolioOverviewPage),
  },
  {
    path: 'stocks/:symbol',
    data: { assetClass: 'Stock' },
    loadComponent: () => import('./features/asset-detail/asset-detail.page').then((m) => m.AssetDetailPage),
  },
  {
    path: 'crypto',
    data: { assetClass: 'Crypto' },
    loadComponent: () =>
      import('./features/portfolio/portfolio-overview.page').then((m) => m.PortfolioOverviewPage),
  },
  {
    path: 'crypto/:symbol',
    data: { assetClass: 'Crypto' },
    loadComponent: () => import('./features/asset-detail/asset-detail.page').then((m) => m.AssetDetailPage),
  },
  {
    path: 'transactions',
    loadComponent: () => import('./features/transactions/transactions.page').then((m) => m.TransactionsPage),
  },
  {
    path: 'assets',
    loadComponent: () =>
      import('./features/asset-management/asset-management.page').then((m) => m.AssetManagementPage),
  },
  { path: '**', redirectTo: 'stocks' },
];
