import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';

import { NotificationService } from '../../core/notifications/notification.service';
import { PriceStore } from '../../core/prices/price-store';
import { describeRefreshOutcome } from '../../core/prices/refresh-outcome';
import {
  buildMarketRefreshRows,
  describeIdleRefreshPreview,
} from '../../core/prices/refresh-panel';
import { formatRelativeTime, isRefreshStale } from '../../shared/time/relative-time';

const TICK_INTERVAL_MS = 15_000;

/**
 * Toolbar top-right: a relative "Updated N ago" label (now itself a
 * `mat-menu` trigger opening a details panel — one row per market/provider),
 * a manual refresh button, a 429-cooldown countdown, and an amber stale
 * warning.
 *
 * The D5 "why nothing moved" message no longer lives in a hover-only tooltip
 * discoverable only AFTER a click — see:
 *  - `tooltip()` below, which now previews what a press will do BEFORE it
 *    happens (`describeIdleRefreshPreview`), and
 *  - `refresh()`, which surfaces the actual outcome through
 *    `NotificationService` once the click completes.
 */
@Component({
  selector: 'app-refresh-indicator',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule],
  templateUrl: './refresh-indicator.html',
  styleUrl: './refresh-indicator.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RefreshIndicator {
  private readonly priceStore = inject(PriceStore);
  private readonly notifications = inject(NotificationService);
  private readonly now = signal(Date.now());

  /**
   * True only between a click on THIS component's button and that click's
   * own result landing. Gates the post-refresh toast in the effect below so
   * a snackbar only ever fires for a refresh the user actually clicked here —
   * `PriceStore` is root-provided and `lastRefreshResult`/`lastError` are
   * shared signals, so watching them unconditionally would also toast for
   * any future non-user-initiated caller of `refreshNow()`, or double-toast
   * if more than one indicator were ever mounted.
   */
  private readonly awaitingOwnResult = signal(false);

  constructor() {
    const timer = setInterval(() => this.now.set(Date.now()), TICK_INTERVAL_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));

    effect(() => {
      if (!this.awaitingOwnResult()) {
        return;
      }
      const result = this.priceStore.lastRefreshResult();
      if (result) {
        this.awaitingOwnResult.set(false);
        const message = describeRefreshOutcome(result, this.priceStore.status());
        const failed = result.sources.some((source) => source.attempted && !source.success);
        if (failed) {
          this.notifications.error(message);
        } else {
          this.notifications.info(message);
        }
        return;
      }
      const error = this.priceStore.lastError();
      if (error) {
        this.awaitingOwnResult.set(false);
        this.notifications.error(error);
      }
    });
  }

  readonly connectionState = this.priceStore.connectionState;
  readonly refreshing = this.priceStore.refreshing;
  readonly cooldownSecondsRemaining = this.priceStore.cooldownSecondsRemaining;
  readonly status = this.priceStore.status;

  readonly relativeLabel = computed(
    () => `Updated ${formatRelativeTime(this.priceStore.lastRefreshedAt(), this.now())}`,
  );

  readonly isStale = computed(() => isRefreshStale(this.priceStore.status(), this.now()));

  readonly isDegraded = this.priceStore.isDegraded;

  /** One row per market/provider for the details panel — null status means "waiting", not "closed". */
  readonly marketRows = computed(() => {
    const status = this.priceStore.status();
    return status ? buildMarketRefreshRows(status, this.now()) : [];
  });

  readonly panelTriggerLabel = computed(() => `Refresh details. ${this.relativeLabel()}.`);

  /** Pre-click preview of what a press will do RIGHT NOW — the point of this whole feature. */
  private readonly idlePreview = computed(() =>
    describeIdleRefreshPreview(this.priceStore.status()),
  );

  readonly tooltip = computed(() => {
    if (this.refreshing()) {
      return 'Refreshing…';
    }
    const cooldown = this.cooldownSecondsRemaining();
    if (cooldown !== null) {
      return `Refresh available in ${cooldown}s.`;
    }
    if (this.isDegraded()) {
      return 'Live prices are unavailable — reconnecting… showing the last known refresh status via periodic polling.';
    }
    if (this.isStale()) {
      return `Prices look stale — a refresh is overdue. ${this.idlePreview()}`;
    }
    return this.idlePreview();
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
    this.awaitingOwnResult.set(true);
    this.priceStore.refreshNow();
  }
}
