import { BreakpointObserver } from '@angular/cdk/layout';
import { Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';
import { HealthStrip } from './layout/health-strip/health-strip';
import { NAV_ITEMS } from './layout/nav-items';

/** Below this width the menu slides over the page instead of standing beside it. */
export const NARROW_QUERY = '(max-width: 899.98px)';

/** The app shell: toolbar, health strip, side menu and the routed page. */
@Component({
  selector: 'app-root',
  imports: [
    MatButtonModule,
    MatIconModule,
    MatListModule,
    MatSidenavModule,
    MatToolbarModule,
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    HealthStrip,
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  protected readonly navItems = NAV_ITEMS;

  protected readonly isNarrow = toSignal(
    inject(BreakpointObserver)
      .observe(NARROW_QUERY)
      .pipe(map((state) => state.matches)),
    { initialValue: false },
  );
}
