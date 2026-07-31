import { ChangeDetectionStrategy, Component } from '@angular/core';

import { AppShell } from './layout/app-shell/app-shell';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [AppShell],
  template: `<app-shell />`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {}
