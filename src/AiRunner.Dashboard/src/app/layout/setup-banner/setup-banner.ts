import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter, map } from 'rxjs';
import { InlineNote } from '../../shared/inline-note/inline-note';
import { HealthStore } from '../health-store';
import { SETUP_PATH } from '../nav-items';

/**
 * The banner above every screen while the runner has no GitBucket token (`health.isSetupNeeded`, decided by the
 * runner): nothing is polled and no job runs until one is saved on the Setup screen. Reads the shell's health, so
 * `/api/health` is not polled twice.
 */
@Component({
  selector: 'app-setup-banner',
  imports: [InlineNote, RouterLink],
  template: `
    @if (isShown()) {
      <app-inline-note icon="key" data-testid="setup-banner">
        The runner has no GitBucket token yet: it polls nothing and runs no job.
        <a [routerLink]="setupPath">Open Setup</a> to add one.
      </app-inline-note>
    }
  `,
  styles: `
    :host {
      display: block;
    }

    app-inline-note {
      margin-bottom: var(--app-space-4);
    }

    a {
      color: inherit;
      font: var(--mat-sys-label-large);
    }
  `,
})
export class SetupBanner {
  private readonly router = inject(Router);
  private readonly health = inject(HealthStore).health.value;

  protected readonly setupPath = SETUP_PATH;

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      map((event) => event.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );

  private readonly isOnSetup = computed(() => {
    const tree = this.router.parseUrl(this.url());
    const path =
      '/' +
      (tree.root.children['primary']?.segments.map((segment) => segment.path).join('/') ?? '');

    return path === SETUP_PATH;
  });

  protected readonly isShown = computed(
    () => this.health()?.isSetupNeeded === true && !this.isOnSetup(),
  );
}
