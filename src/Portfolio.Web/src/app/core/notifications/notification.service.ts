import { Injectable, inject } from '@angular/core';
import { MatSnackBar, MatSnackBarConfig } from '@angular/material/snack-bar';

import { AppSnackbar, AppSnackbarData, SnackbarTone } from '../../shared/snackbar/app-snackbar';

/**
 * Success/info are a transient acknowledgement of something that already
 * finished — long enough to read, short enough not to linger over a screen
 * the user has already moved past.
 */
export const SUCCESS_INFO_DURATION_MS = 4000;

/**
 * Errors stay up twice as long AND carry a Dismiss action, because a failure
 * message the user misses is the one case where the toast has failed at its
 * job — the action it's reporting on did NOT happen, and (per the page-state
 * vs action-outcome split below) there is no persistent banner left behind
 * to fall back on once the toast is gone.
 */
export const ERROR_DURATION_MS = 8000;

/**
 * Root-provided wrapper around `MatSnackBar` — the single entry point for
 * "a discrete action the user just took finished, here's how" feedback.
 *
 * ### Page state vs. action outcome — the rule this service exists to keep
 * - **Page-level state** (a resource that is loading / empty / failed to
 *   load) is persistent, needs a Retry action sitting next to the content it
 *   describes, and stays `app-state-message` — it must never be a toast that
 *   can disappear before anyone reads it.
 * - **Discrete action outcome** (a click that just finished — save, delete,
 *   toggle) is transient and non-blocking. That is what `success` / `error`
 *   / `info` below are for.
 * - **Form validation** stays inline in the dialog (`serverError` signal +
 *   `mat-error` per field): the dialog stays open on failure, so the message
 *   belongs right next to the field that caused it, not in a toast the user
 *   has to go correlate back to the form.
 *
 * Opens via `MatSnackBar.openFromComponent` (never `.open()`), so the
 * rendered markup is this app's own `AppSnackbar` — icon, wording and a
 * token-driven surface — rather than Material's default dark text-only slab.
 *
 * NOTE: `MatSnackBar` dismisses whichever snackbar is currently open before
 * it opens a new one. That is the wanted behaviour here — one outcome
 * message at a time, always the most recent — not a bug to work around with
 * a queue.
 */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  private readonly snackBar = inject(MatSnackBar);

  /** e.g. "AAPL is now tracked." */
  success(message: string): void {
    this.open('success', message, SUCCESS_INFO_DURATION_MS, 'polite');
  }

  /** e.g. "Couldn't delete AAPL — it is unchanged." Carries a Dismiss action. */
  error(message: string): void {
    this.open('error', message, ERROR_DURATION_MS, 'assertive', 'Dismiss');
  }

  /** e.g. "That transaction no longer exists — the list has been refreshed." */
  info(message: string): void {
    this.open('info', message, SUCCESS_INFO_DURATION_MS, 'polite');
  }

  private open(
    tone: SnackbarTone,
    message: string,
    duration: number,
    politeness: 'polite' | 'assertive',
    dismissLabel?: string,
  ): void {
    const data: AppSnackbarData = { tone, message, dismissLabel };
    const config: MatSnackBarConfig<AppSnackbarData> = {
      data,
      duration,
      politeness,
      horizontalPosition: 'center',
      // Top, not Material's default bottom — see the `.app-snackbar-panel`
      // margin-top rule in styles.scss, which offsets the toast below the
      // sticky toolbar so a top-anchored toast (placed against the viewport
      // edge, not the app shell's layout) doesn't render over the title or,
      // on handset, the nav menu button.
      verticalPosition: 'top',
      // Not the tone alone — a base class too, so styles.scss can target
      // "any app snackbar" (border/elevation/measure) separately from "this
      // tone's accent bar" without repeating the shared rules three times.
      panelClass: ['app-snackbar-panel', `app-snackbar-panel--${tone}`],
    };

    this.snackBar.openFromComponent(AppSnackbar, config);
  }
}
