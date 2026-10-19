import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, Signal, inject, signal } from '@angular/core';

/** How often a screen refreshes its data (spec: every 3 s while visible). */
export const POLL_INTERVAL_MS = 3000;

/** The live result of an endpoint that is polled while the browser tab is visible. */
export interface PollResource<T> {
  /** The last answer. Kept when a later call fails, so the screen keeps showing data next to the error. */
  readonly value: Signal<T | undefined>;
  /** Text of the last failure; cleared by the next success. */
  readonly error: Signal<string | undefined>;
  /** True until the first answer (value or error) arrived: show the loading state only then. */
  readonly isLoading: Signal<boolean>;
  /**
   * Load now (a retry button, a changed filter), then keep polling. During a call it loads once more right after
   * that call, so a new filter is never answered with the old one's result alone.
   */
  reload(): void;
}

/**
 * Polls `load` every `intervalMs` while the tab is visible: the one refresh mechanism of every screen. It waits for
 * an answer before it schedules the next call (no pile-up when the runner is slow), pauses while the tab is hidden,
 * loads at once when the tab comes back, and stops when the owning component or service is destroyed.
 * Call it in an injection context (a field initializer of a component or service).
 */
export function pollResource<T>(
  load: () => Promise<T>,
  intervalMs = POLL_INTERVAL_MS,
): PollResource<T> {
  const document = inject(DOCUMENT);
  const destroyRef = inject(DestroyRef);

  const value = signal<T | undefined>(undefined);
  const error = signal<string | undefined>(undefined);
  const isLoading = signal(true);

  let timer: ReturnType<typeof setTimeout> | undefined;
  let isDestroyed = false;
  let isRunning = false;
  let isRerunWanted = false;

  const schedule = () => {
    clearTimeout(timer);
    timer = undefined;

    if (!isDestroyed && !document.hidden) {
      timer = setTimeout(run, intervalMs);
    }
  };

  const run = async () => {
    if (isDestroyed) {
      return;
    }

    if (isRunning) {
      isRerunWanted = true;

      return;
    }

    isRunning = true;
    clearTimeout(timer);

    try {
      const result = await load();

      if (!isDestroyed) {
        value.set(result);
        error.set(undefined);
      }
    } catch (failure) {
      if (!isDestroyed) {
        error.set(describeError(failure));
      }
    } finally {
      isRunning = false;
      isLoading.set(false);

      if (isRerunWanted) {
        isRerunWanted = false;
        void run();
      } else {
        schedule();
      }
    }
  };

  const onVisibilityChange = () => {
    if (document.hidden) {
      clearTimeout(timer);
      timer = undefined;
    } else {
      void run();
    }
  };

  document.addEventListener('visibilitychange', onVisibilityChange);
  destroyRef.onDestroy(() => {
    isDestroyed = true;
    clearTimeout(timer);
    document.removeEventListener('visibilitychange', onVisibilityChange);
  });

  if (!document.hidden) {
    void run();
  }

  return {
    value: value.asReadonly(),
    error: error.asReadonly(),
    isLoading: isLoading.asReadonly(),
    reload: () => void run(),
  };
}

/**
 * Short text for a failed call. Status 0 means the browser got no answer at all: the runner is down. A 400 carries
 * the API's own sentence (`ApiError.message`), which says more than the status.
 */
export function describeError(failure: unknown): string {
  if (failure instanceof HttpErrorResponse) {
    if (failure.status === 0) {
      return 'The runner is not reachable.';
    }

    const apiMessage = (failure.error as { message?: unknown } | null)?.message;
    if (typeof apiMessage === 'string' && apiMessage.length > 0) {
      return apiMessage;
    }

    return `The runner answered ${failure.status} ${failure.statusText}`.trim();
  }

  return failure instanceof Error ? failure.message : String(failure);
}
