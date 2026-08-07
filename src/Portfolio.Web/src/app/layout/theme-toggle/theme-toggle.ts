import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';

import { ThemePreference, ThemeStore } from '../../core/theme/theme-store';

interface ThemeOption {
  value: ThemePreference;
  label: string;
  icon: string;
}

const OPTIONS: readonly ThemeOption[] = [
  { value: 'light', label: 'Light', icon: 'light_mode' },
  { value: 'dark', label: 'Dark', icon: 'dark_mode' },
  { value: 'system', label: 'Follow OS', icon: 'brightness_auto' },
];

/**
 * Toolbar top-right, beside the refresh indicator: the D25 Light / Dark /
 * Follow OS toggle. A menu rather than a plain cycling button, so all three
 * states (including the non-obvious "Follow OS") are named rather than
 * hidden behind repeated clicks — and so the current choice is always
 * visible at a glance from the icon alone (never a bare colour swap, per the
 * project's own "not colour alone" rule).
 */
@Component({
  selector: 'app-theme-toggle',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule],
  templateUrl: './theme-toggle.html',
  styleUrl: './theme-toggle.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ThemeToggle {
  private readonly themeStore = inject(ThemeStore);

  readonly options = OPTIONS;
  readonly preference = this.themeStore.preference;

  readonly current = computed(
    () => OPTIONS.find((option) => option.value === this.preference()) ?? OPTIONS[2],
  );

  readonly buttonLabel = computed(() => `Theme: ${this.current().label}. Click to change.`);

  choose(value: ThemePreference): void {
    this.themeStore.setPreference(value);
  }
}
