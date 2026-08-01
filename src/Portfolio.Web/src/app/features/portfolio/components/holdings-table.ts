import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';

import { HoldingDto } from '../../../core/api/models';
import { MoneyPipe } from '../../../shared/pipes/money.pipe';
import { QuantityPipe } from '../../../shared/pipes/quantity.pipe';
import { GainLoss } from '../../../shared/gain-loss/gain-loss';

/**
 * The table-view twin of the allocation pie and summary tiles — every number
 * a reader might want is here in plain text, not gated behind hovering a
 * chart (anti-patterns.md: "no table view / colour-only encoding").
 *
 * A holding with `currentPriceUsd: null` (a position with no live quote yet)
 * renders "Awaiting first price" rather than the naive -100% the backend's
 * own numbers would otherwise produce (marketValueUsd 0 minus a real cost
 * basis) — see tracker.md and PortfolioSummaryService's own doc comment.
 */
@Component({
  selector: 'app-holdings-table',
  standalone: true,
  imports: [RouterLink, MoneyPipe, QuantityPipe, GainLoss],
  templateUrl: './holdings-table.html',
  styleUrl: './holdings-table.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HoldingsTable {
  readonly holdings = input.required<HoldingDto[]>();
  readonly basePath = input.required<string>();

  hasPrice(holding: HoldingDto): boolean {
    return holding.currentPriceUsd !== null;
  }
}
