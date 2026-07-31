import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

export type StateMessageVariant = 'loading' | 'empty' | 'error';

/**
 * One shared presentational shell for the three non-happy-path states every
 * data-backed view must handle explicitly (loading / empty / error) — so an
 * empty portfolio renders a helpful call to action, and a failed request
 * renders a retry, rather than either rendering a blank page.
 */
@Component({
  selector: 'app-state-message',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatProgressSpinnerModule],
  templateUrl: './state-message.html',
  styleUrl: './state-message.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StateMessage {
  readonly variant = input.required<StateMessageVariant>();
  readonly title = input.required<string>();
  readonly description = input<string>('');
  readonly actionLabel = input<string | null>(null);

  readonly action = output<void>();
}
