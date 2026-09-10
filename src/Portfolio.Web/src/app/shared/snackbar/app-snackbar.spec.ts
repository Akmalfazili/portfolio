import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_SNACK_BAR_DATA, MatSnackBarRef } from '@angular/material/snack-bar';

import { AppSnackbar, AppSnackbarData } from './app-snackbar';

describe('AppSnackbar', () => {
  let fixture: ComponentFixture<AppSnackbar>;
  let snackBarRef: { dismiss: ReturnType<typeof vi.fn> };

  function setup(data: AppSnackbarData): void {
    snackBarRef = { dismiss: vi.fn() };
    TestBed.configureTestingModule({
      imports: [AppSnackbar],
      providers: [
        { provide: MAT_SNACK_BAR_DATA, useValue: data },
        { provide: MatSnackBarRef, useValue: snackBarRef },
      ],
    });
    fixture = TestBed.createComponent(AppSnackbar);
    fixture.detectChanges();
  }

  function iconName(): string | null {
    return fixture.nativeElement.querySelector('mat-icon')?.textContent?.trim() ?? null;
  }

  function dismissButton(): HTMLButtonElement | null {
    return fixture.nativeElement.querySelector('button');
  }

  it('renders check_circle and the message for a success toast, with no dismiss button', () => {
    setup({ tone: 'success', message: 'AAPL is now tracked.' });

    expect(fixture.nativeElement.textContent).toContain('AAPL is now tracked.');
    expect(iconName()).toBe('check_circle');
    expect(dismissButton()).toBeNull();
  });

  it('renders info for an info toast, with no dismiss button', () => {
    setup({
      tone: 'info',
      message: 'That transaction no longer exists — the list has been refreshed.',
    });

    expect(iconName()).toBe('info');
    expect(dismissButton()).toBeNull();
  });

  it('renders error_outline and a Dismiss button for an error toast — never colour alone', () => {
    setup({ tone: 'error', message: "Couldn't delete AAPL.", dismissLabel: 'Dismiss' });

    expect(iconName()).toBe('error_outline');
    const button = dismissButton();
    expect(button?.textContent?.trim()).toBe('Dismiss');
  });

  it('dismisses via the snack bar ref when the Dismiss button is clicked', () => {
    setup({ tone: 'error', message: 'boom', dismissLabel: 'Dismiss' });

    dismissButton()?.click();

    expect(snackBarRef.dismiss).toHaveBeenCalledTimes(1);
  });

  it('applies the tone-scoped host class so styles.scss can colour the icon per tone', () => {
    setup({ tone: 'success', message: 'ok' });
    expect(fixture.nativeElement.querySelector('.app-snackbar--success')).toBeTruthy();
  });

  /**
   * Regression guard for a contrast defect this button once had: left as a
   * plain, unstyled `mat-button`, its label falls through to
   * `--mat-button-text-label-text-color`'s own default (`--mat-sys-primary`,
   * i.e. the per-section `--ui-color-accent`), which fails WCAG AA for
   * Crypto's orange on this toast's near-white surface — and silently
   * changes the button's readability depending on which section the user is
   * in. `app-snackbar.scss` fixes this by repointing the button's own
   * component token to the fixed, section-independent `--ui-color-on-surface`.
   *
   * This suite's jsdom environment does not reliably resolve CSS custom
   * properties through `getComputedStyle` for styles Angular injects via a
   * component's own `<style>` tag, so asserting the *computed* colour here
   * would be unstable (and this project has no `@types/node` / raw-import
   * setup to assert against the stylesheet source instead). What IS stable
   * and DOM-observable is that the button carries the class the override
   * targets — a weaker guarantee than pinning the colour itself, but one
   * that at least fails loudly if a future change renames or removes the
   * class the SCSS rule depends on.
   */
  it('renders the Dismiss button with the hook class its neutral-colour override targets', () => {
    setup({ tone: 'error', message: 'boom', dismissLabel: 'Dismiss' });
    expect(dismissButton()?.classList.contains('app-snackbar__dismiss')).toBe(true);
  });
});
