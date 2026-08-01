import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { HoldingDto } from '../../../core/api/models';
import { MoneyPipe } from '../../../shared/pipes/money.pipe';
import { GainLoss } from '../../../shared/gain-loss/gain-loss';
import { StatTile } from '../../../shared/stat-tile/stat-tile';

/**
 * Cost basis, market value, and both absolute and percentage gain/loss for
 * one asset. Used on EVERY asset-detail page — for crypto it IS the whole
 * content besides the price header (no chart, no range selector, per the
 * crypto-scope decision); for stocks it sits alongside the performance chart.
 *
 * A position with no live quote yet (`currentPriceUsd: null`) shows "Awaiting
 * first price" instead of the naive -100% the raw numbers would otherwise
 * read as (marketValueUsd 0 minus a real cost basis) — see tracker.md.
 */
@Component({
  selector: 'app-gain-loss-card',
  standalone: true,
  imports: [MoneyPipe, GainLoss, StatTile],
  templateUrl: './gain-loss-card.html',
  styleUrl: './gain-loss-card.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GainLossCard {
  readonly holding = input.required<HoldingDto>();

  readonly hasPrice = computed(() => this.holding().currentPriceUsd !== null);
}
