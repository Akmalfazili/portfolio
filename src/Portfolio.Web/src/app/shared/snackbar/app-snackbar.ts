import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MAT_SNACK_BAR_DATA, MatSnackBarRef } from '@angular/material/snack-bar';

export type SnackbarTone = 'success' | 'error' | 'info';

export interface AppSnackbarData {
  readonly tone: SnackbarTone;
  readonly message: string;
  /** Only ever set for the error tone — see NotificationService.error(). */
  readonly dismissLabel?: string;
}

const TONE_ICON: Record<SnackbarTone, string> = {
  success: 'check_circle',
  error: 'error_outline',
  info: 'info',
};

/**
 * Presentational body rendered inside every toast `NotificationService`
 * opens (via `MatSnackBar.openFromComponent`, not Material's default
 * text-only snack bar). Never colour alone: the icon plus the wording carry
 * the meaning, the same convention `shared/gain-loss` uses for gain/loss —
 * the tone's colour also appears as a leading accent bar on the outer
 * Material surface (see the `.app-snackbar-panel--*` rules in styles.scss;
 * that part lives outside this component's own encapsulation, since the
 * surface is Material's own element, not ours).
 */
@Component({
  selector: 'app-snackbar',
  standalone: true,
  imports: [MatButtonModule, MatIconModule],
  templateUrl: './app-snackbar.html',
  styleUrl: './app-snackbar.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppSnackbar {
  protected readonly data = inject<AppSnackbarData>(MAT_SNACK_BAR_DATA);
  private readonly snackBarRef = inject(MatSnackBarRef<AppSnackbar>);

  protected readonly icon = TONE_ICON[this.data.tone];

  dismiss(): void {
    this.snackBarRef.dismiss();
  }
}
