import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * A single headline number — label, value, and an optional delta/description
 * projected as content (typically an `<app-gain-loss>`). Per the dataviz
 * skill's stat-tile contract: proportional figures on the value (never
 * `tabular-nums` — that's reserved for columns that must align), the value
 * in the same sans as everything else, one glance, no chart needed for a
 * single current number.
 */
@Component({
  selector: 'app-stat-tile',
  standalone: true,
  templateUrl: './stat-tile.html',
  styleUrl: './stat-tile.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StatTile {
  readonly label = input.required<string>();
  readonly value = input.required<string>();
}
