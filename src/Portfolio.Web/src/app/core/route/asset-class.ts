import { RouterStateSnapshot } from '@angular/router';
import { AssetClass } from '../api/models';

/**
 * Walks the active route tree for the deepest `data.assetClass` — the single
 * place that answers "which section (Stocks/Crypto) is the user in", used to
 * drive both the shell's accent token swap and any page that needs it.
 */
export function resolveAssetClass(state: RouterStateSnapshot): AssetClass | null {
  let route = state.root;
  let result: AssetClass | null = null;

  while (route) {
    const candidate = route.data['assetClass'];
    if (candidate === 'Stock' || candidate === 'Crypto') {
      result = candidate;
    }
    if (!route.firstChild) {
      break;
    }
    route = route.firstChild;
  }

  return result;
}
