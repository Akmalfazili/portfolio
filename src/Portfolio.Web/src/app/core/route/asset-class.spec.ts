import { RouterStateSnapshot, ActivatedRouteSnapshot } from '@angular/router';
import { resolveAssetClass } from './asset-class';

function routeWithData(
  data: Record<string, unknown>,
  firstChild: ActivatedRouteSnapshot | null = null,
) {
  return { data, firstChild } as unknown as ActivatedRouteSnapshot;
}

describe('resolveAssetClass', () => {
  it('returns Crypto for a /crypto route', () => {
    const leaf = routeWithData({ assetClass: 'Crypto' });
    const state = { root: leaf } as RouterStateSnapshot;
    expect(resolveAssetClass(state)).toBe('Crypto');
  });

  it('returns Stock for a nested /stocks/:symbol route', () => {
    const leaf = routeWithData({ assetClass: 'Stock' });
    const root = routeWithData({}, leaf);
    const state = { root } as RouterStateSnapshot;
    expect(resolveAssetClass(state)).toBe('Stock');
  });

  it('returns null for a route with no assetClass data, e.g. /transactions', () => {
    const leaf = routeWithData({});
    const root = routeWithData({}, leaf);
    const state = { root } as RouterStateSnapshot;
    expect(resolveAssetClass(state)).toBeNull();
  });
});
