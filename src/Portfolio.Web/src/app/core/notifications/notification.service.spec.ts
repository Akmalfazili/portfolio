import { TestBed } from '@angular/core/testing';
import { MatSnackBar, MatSnackBarConfig } from '@angular/material/snack-bar';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { NotificationService, SUCCESS_INFO_DURATION_MS, ERROR_DURATION_MS } from './notification.service';
import { AppSnackbar, AppSnackbarData } from '../../shared/snackbar/app-snackbar';

describe('NotificationService', () => {
  let service: NotificationService;
  let openFromComponent: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideNoopAnimations()],
    });
    service = TestBed.inject(NotificationService);
    openFromComponent = vi
      .spyOn(TestBed.inject(MatSnackBar), 'openFromComponent')
      .mockReturnValue({} as ReturnType<MatSnackBar['openFromComponent']>);
  });

  function lastCall(): [typeof AppSnackbar, MatSnackBarConfig<AppSnackbarData>] {
    const call = openFromComponent.mock.calls.at(-1);
    if (!call) {
      throw new Error('openFromComponent was not called');
    }
    return [call[0], call[1]];
  }

  it('opens AppSnackbar (never MatSnackBar.open — the markup must be ours)', () => {
    service.success('AAPL is now tracked.');

    expect(openFromComponent).toHaveBeenCalledTimes(1);
    const [component] = lastCall();
    expect(component).toBe(AppSnackbar);
  });

  it('success: polite, the short duration, no dismiss label, tone classes and centered/top position', () => {
    service.success('AAPL is now tracked.');

    const [, config] = lastCall();
    expect(config.data).toEqual({ tone: 'success', message: 'AAPL is now tracked.', dismissLabel: undefined });
    expect(config.duration).toBe(SUCCESS_INFO_DURATION_MS);
    expect(config.politeness).toBe('polite');
    expect(config.panelClass).toEqual(['app-snackbar-panel', 'app-snackbar-panel--success']);
    expect(config.horizontalPosition).toBe('center');
    // Top, not Material's default bottom — offset below the sticky toolbar
    // via the `.app-snackbar-panel` margin-top rule in styles.scss.
    expect(config.verticalPosition).toBe('top');
  });

  it('info: same shape as success, tagged with the info tone', () => {
    service.info('That transaction no longer exists — the list has been refreshed.');

    const [, config] = lastCall();
    expect(config.data).toEqual({
      tone: 'info',
      message: 'That transaction no longer exists — the list has been refreshed.',
      dismissLabel: undefined,
    });
    expect(config.duration).toBe(SUCCESS_INFO_DURATION_MS);
    expect(config.politeness).toBe('polite');
    expect(config.panelClass).toEqual(['app-snackbar-panel', 'app-snackbar-panel--info']);
  });

  it('error: assertive, the long duration, and a Dismiss label — a missed failure is the one case the toast must not fail at', () => {
    service.error("Couldn't delete AAPL — it is unchanged.");

    const [, config] = lastCall();
    expect(config.data).toEqual({
      tone: 'error',
      message: "Couldn't delete AAPL — it is unchanged.",
      dismissLabel: 'Dismiss',
    });
    expect(config.duration).toBe(ERROR_DURATION_MS);
    expect(config.politeness).toBe('assertive');
    expect(config.panelClass).toEqual(['app-snackbar-panel', 'app-snackbar-panel--error']);
  });

  it('the error duration is longer than the success/info duration', () => {
    expect(ERROR_DURATION_MS).toBeGreaterThan(SUCCESS_INFO_DURATION_MS);
  });
});
