import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { PriceStore } from '../../core/prices/price-store';
import { describeRefreshOutcome } from '../../core/prices/refresh-outcome';
import { formatRelativeTime, isRefreshStale } from '../../shared/time/relative-time';

const TICK_INTERVAL_MS = 15_000;

/**
 * Toolbar top-right: a relative "Updated N ago" label driven by a ticking
 * signal, a manual refresh button, a 429-cooldown countdown, an amber stale
 * warning, and the D5 "why nothing moved" message after a manual click.
 */
@Component({
  selector: 'app-refresh-indicator',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  templateUrl: './refresh-indicator.html',
  styleUrl: './refresh-indicator.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RefreshIndicator {
  private readonly priceStore = inject(PriceStore);
  private readonly now = signal(Date.now());

  constructor() {
    const timer = setInterval(() => this.now.set(Date.now()), TICK_INTERVAL_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  readonly connectionState = this.priceStore.connectionState;
  readonly refreshing = this.priceStore.refreshing;
  readonly cooldownSecondsRemaining = this.priceStore.cooldownSecondsRemaining;

  readonly relativeLabel = computed(() => `Updated ${formatRelativeTime(this.priceStore.lastRefreshedAt(), this.now())}`);

  readonly isStale = computed(() => isRefreshStale(this.priceStore.status(), this.now()));

  readonly isDegraded = this.priceStore.isDegraded;

  readonly outcomeMessage = computed(() => {
    const result = this.priceStore.lastRefreshResult();
    return result ? describeRefreshOutcome(result, this.priceStore.status()) : null;
  });

  readonly tooltip = computed(() => {
    if (this.isDegraded()) {
      return 'Live prices are unavailable — reconnecting… showing the last known refresh status via periodic polling.';
    }
    const outcome = this.outcomeMessage();
    if (outcome) {
      return outcome;
    }
    return this.isStale() ? 'Prices look stale — a refresh is overdue.' : 'Prices are up to date.';
  });

  readonly disabled = computed(() => this.refreshing() || this.cooldownSecondsRemaining() !== null);

  readonly buttonLabel = computed(() => {
    const cooldown = this.cooldownSecondsRemaining();
    if (cooldown !== null) {
      return `Refresh available in ${cooldown}s`;
    }
    return this.refreshing() ? 'Refreshing…' : 'Refresh prices now';
  });

  refresh(): void {
    this.priceStore.refreshNow();
  }
}
