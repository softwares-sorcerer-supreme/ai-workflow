import { Component, input } from '@angular/core';

/**
 * The top of every page: one h1 title, an optional subtitle, and actions on the right (filters, toggles) projected
 * with the `actions` attribute: `<app-page-header title="Jobs"><mat-button-toggle-group actions>...`.
 */
@Component({
  selector: 'app-page-header',
  template: `
    <div class="text">
      <h1>{{ title() }}</h1>
      @if (subtitle()) {
        <p class="app-muted">{{ subtitle() }}</p>
      }
    </div>
    <div class="actions"><ng-content select="[actions]" /></div>
  `,
  styles: `
    :host {
      display: flex;
      flex-wrap: wrap;
      align-items: flex-end;
      justify-content: space-between;
      gap: var(--app-space-3) var(--app-space-4);
      margin-bottom: var(--app-space-5);
    }

    h1 {
      margin: 0;
      font: var(--mat-sys-headline-small);
    }

    p {
      margin: var(--app-space-1) 0 0;
      font: var(--mat-sys-body-medium);
    }

    .actions {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--app-space-2);
    }

    .actions:empty {
      display: none;
    }
  `,
})
export class PageHeader {
  readonly title = input.required<string>();
  readonly subtitle = input<string>();
}
