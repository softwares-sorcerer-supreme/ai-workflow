import { DestroyRef, Injectable, inject, signal } from '@angular/core';

/**
 * One shared "now" that ticks every second, so relative times ("12 s ago") stay current without each component
 * running its own timer. Tests replace it with a fixed clock.
 */
@Injectable({ providedIn: 'root' })
export class Clock {
  private readonly nowMs = signal(Date.now());

  /** The current time in milliseconds, as a signal. */
  readonly now = this.nowMs.asReadonly();

  constructor() {
    const timer = setInterval(() => this.nowMs.set(Date.now()), 1000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }
}
