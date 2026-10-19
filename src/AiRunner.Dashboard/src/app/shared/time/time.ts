import { Component, computed, inject, input } from '@angular/core';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Clock } from './clock';
import { formatFull, formatLocal, formatRelative } from './time-format';

/**
 * A point in time from the API (UTC ISO-8601), shown in local time. `relative` shows "5 min ago", `absolute` the
 * local time (date added when not today). The tooltip always has the full local date and time. Null shows "-".
 */
@Component({
  selector: 'app-time',
  imports: [MatTooltipModule],
  template: `
    @if (value(); as iso) {
      <time [attr.datetime]="iso" [matTooltip]="full()">{{ text() }}</time>
    } @else {
      <span class="app-muted">-</span>
    }
  `,
})
export class AppTime {
  readonly value = input<string | null | undefined>();
  readonly mode = input<'relative' | 'absolute'>('absolute');

  private readonly clock = inject(Clock);

  protected readonly text = computed(() => {
    const iso = this.value();
    const now = this.clock.now();

    if (!iso) {
      return '';
    }

    return this.mode() === 'relative' ? formatRelative(iso, now) : formatLocal(iso, now);
  });

  protected readonly full = computed(() => {
    const iso = this.value();

    return iso ? formatFull(iso) : '';
  });
}
