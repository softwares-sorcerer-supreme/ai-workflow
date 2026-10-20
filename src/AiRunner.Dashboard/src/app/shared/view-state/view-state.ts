import { Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

export type ViewStateKind = 'loading' | 'empty' | 'error';

const DEFAULT_ICONS: Record<ViewStateKind, string> = {
  loading: '',
  empty: 'inbox',
  error: 'error',
};

/**
 * What a panel shows when it has no data to show: still loading, nothing there, or the call failed. Use it inside
 * a card or a page body; the data table uses it for its own states.
 */
@Component({
  selector: 'app-view-state',
  imports: [MatButtonModule, MatIconModule, MatProgressSpinnerModule],
  template: `
    @if (kind() === 'loading') {
      <mat-progress-spinner mode="indeterminate" diameter="32" aria-label="Loading" />
    } @else {
      <mat-icon class="icon" aria-hidden="true">{{ iconName() }}</mat-icon>
    }
    @if (heading()) {
      <p class="title">{{ heading() }}</p>
    }
    @if (message()) {
      <p class="message app-muted">{{ message() }}</p>
    }
    @if (kind() === 'error' && canRetry()) {
      <button mat-stroked-button type="button" (click)="retry.emit()">
        <mat-icon>refresh</mat-icon>
        Try again
      </button>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: var(--app-space-2);
      padding: var(--app-space-6) var(--app-space-4);
      text-align: center;
    }

    .icon {
      width: 40px;
      height: 40px;
      font-size: 40px;
      color: var(--mat-sys-on-surface-variant);
    }

    :host(.kind-error) .icon {
      color: var(--mat-sys-error);
    }

    p {
      margin: 0;
      max-width: 48ch;
    }

    .title {
      font: var(--mat-sys-title-small);
    }
  `,
  host: {
    '[class]': '"kind-" + kind()',
    '[attr.role]': 'kind() === "error" ? "alert" : "status"',
  },
})
export class ViewState {
  readonly kind = input.required<ViewStateKind>();
  /** Short line in bold, e.g. "No poll errors". Defaults per kind. */
  readonly title = input<string>();
  /** One sentence of help: what this means or what to do. */
  readonly message = input<string>();
  /** Overrides the default icon (empty: inbox, error: error). */
  readonly icon = input<string>();
  /** Shows "Try again" on the error state. */
  readonly canRetry = input(false);
  readonly retry = output<void>();

  protected readonly iconName = computed(() => this.icon() ?? DEFAULT_ICONS[this.kind()]);

  protected readonly heading = computed(() => {
    const title = this.title();

    if (title) {
      return title;
    }

    switch (this.kind()) {
      case 'loading':
        return '';
      case 'empty':
        return 'Nothing here yet';
      case 'error':
        return 'Could not load this';
    }
  });
}
