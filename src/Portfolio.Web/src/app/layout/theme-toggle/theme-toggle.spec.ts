import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { ThemeToggle } from './theme-toggle';
import { ThemeStore } from '../../core/theme/theme-store';

describe('ThemeToggle', () => {
  let fixture: ComponentFixture<ThemeToggle>;
  let themeStore: ThemeStore;

  beforeEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');

    TestBed.configureTestingModule({
      imports: [ThemeToggle],
      providers: [provideNoopAnimations()],
    });
    themeStore = TestBed.inject(ThemeStore);
    fixture = TestBed.createComponent(ThemeToggle);
    fixture.detectChanges();
  });

  function triggerButton(): HTMLButtonElement {
    return fixture.nativeElement.querySelector('button');
  }

  it('defaults to the "Follow OS" icon and label when no preference is stored', () => {
    expect(triggerButton().getAttribute('aria-label')).toContain('Follow OS');
    expect(fixture.nativeElement.textContent).toContain('brightness_auto');
  });

  it('choosing "dark" updates the trigger icon/label and writes data-theme on <html>', () => {
    fixture.componentInstance.choose('dark');
    fixture.detectChanges();

    expect(triggerButton().getAttribute('aria-label')).toContain('Dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('choosing "light" then "system" genuinely removes data-theme rather than pinning "light"', () => {
    fixture.componentInstance.choose('light');
    fixture.detectChanges();
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');

    fixture.componentInstance.choose('system');
    fixture.detectChanges();

    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
    expect(triggerButton().getAttribute('aria-label')).toContain('Follow OS');
  });

  it('reflects a preference already set on the shared ThemeStore (e.g. by another view)', () => {
    themeStore.setPreference('dark');
    fixture.detectChanges();

    expect(triggerButton().getAttribute('aria-label')).toContain('Dark');
  });
});
