import { Component, computed, inject } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { Tone } from '../../shared/tone';
import { AppTime } from '../../shared/time/time';
import { HealthStore } from '../health-store';

/** Color per health code. The runner decides the state (C# Core/RunnerHealth); this only picks the color. */
export const HEALTH_TONES: Readonly<Record<string, Tone>> = {
  ok: 'success',
  starting: 'info',
  stale: 'warning',
  error: 'danger',
};

/**
 * The line under the toolbar on every screen: is the runner working? State, last poll, worker, poll errors. When
 * the dashboard cannot reach the runner at all, that is the state shown.
 */
@Component({
  selector: 'app-health-strip',
  imports: [MatIconModule, MatTooltipModule, RouterLink, AppTime],
  templateUrl: './health-strip.html',
  styleUrl: './health-strip.scss',
  host: {
    role: 'status',
    'aria-live': 'polite',
    '[class]': '"tone-" + tone()',
  },
})
export class HealthStrip {
  private readonly resource = inject(HealthStore).health;

  protected readonly health = this.resource.value;
  protected readonly isLoading = this.resource.isLoading;
  protected readonly error = this.resource.error;

  /** No answer to the last call: the runner (or this machine's network) is down. */
  protected readonly isUnreachable = computed(() => this.error() !== undefined);

  protected readonly tone = computed<Tone>(() => {
    const health = this.health();

    if (this.isUnreachable()) {
      return 'danger';
    }

    return health ? (HEALTH_TONES[health.state] ?? 'neutral') : 'neutral';
  });

  protected readonly errorsText = computed(() =>
    (this.health()?.errors ?? []).map((error) => `${error.project}: ${error.message}`).join('\n'),
  );
}
