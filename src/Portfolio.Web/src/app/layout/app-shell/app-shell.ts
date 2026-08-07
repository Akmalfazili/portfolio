import { BreakpointObserver } from '@angular/cdk/layout';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { filter, map } from 'rxjs';

import { resolveAssetClass } from '../../core/route/asset-class';
import { RefreshIndicator } from '../refresh-indicator/refresh-indicator';
import { ThemeToggle } from '../theme-toggle/theme-toggle';

const HANDSET_QUERY = '(max-width: 959.98px)';

/**
 * The app shell — sidenav (Stocks / Crypto / Transactions) + toolbar with the
 * refresh indicator. Sets `data-section="stock" | "crypto"` on the document
 * root from the active route's `data.assetClass` so ui.tokens.scss's
 * `--ui-color-accent` (and the Material `--mat-sys-primary` override built on
 * top of it) switch for the whole app, with zero per-section component
 * styling.
 */
@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    MatButtonModule,
    MatIconModule,
    MatListModule,
    MatSidenavModule,
    MatToolbarModule,
    RefreshIndicator,
    ThemeToggle,
  ],
  templateUrl: './app-shell.html',
  styleUrl: './app-shell.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppShell {
  private readonly breakpointObserver = inject(BreakpointObserver);
  private readonly router = inject(Router);

  readonly isHandset = toSignal(
    this.breakpointObserver.observe(HANDSET_QUERY).pipe(map((result) => result.matches)),
    { initialValue: false },
  );

  constructor() {
    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.applySectionFromRoute());

    this.applySectionFromRoute();
  }

  private applySectionFromRoute(): void {
    const assetClass = resolveAssetClass(this.router.routerState.snapshot);
    const section = assetClass === 'Crypto' ? 'crypto' : 'stock';
    document.documentElement.setAttribute('data-section', section);
  }
}
