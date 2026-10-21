import { HttpErrorResponse } from '@angular/common/http';
import { Signal, inject, signal } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { CommandResponse } from '../api/models/command-response';

/** How long the server's answer to a write stays on screen. */
export const WRITE_MESSAGE_MS = 6000;

/** One button that writes: its busy state and the call behind it. */
export interface WriteAction {
  /** True while the call runs: the button is disabled and shows a spinner. */
  readonly isBusy: Signal<boolean>;
  /**
   * Runs the write unless one is already running, shows the server's message in a snack bar and then calls `after`
   * (reload the data, so the screen shows the real state). Resolves to true when the runner accepted it.
   */
  run(call: () => Promise<CommandResponse>): Promise<boolean>;
}

export interface WriteActionOptions {
  /** The sentence shown when a call fails without a message from the runner ("Could not pause the runner."). */
  failure: string;
  /** Runs after every answer, success or failure: reload the resources the write changes. Never update by hand. */
  after?: () => void;
}

/**
 * The one way a button writes: busy while the call runs, the runner's own `message` as a snack bar (on success, and
 * `ApiError.message` on a 403, 409 or any other failure), and no optimistic update: the screen changes only when the
 * next poll (started by `after`) shows the real state. Call it in an injection context (a field initializer).
 */
export function writeAction(options: WriteActionOptions): WriteAction {
  const snackBar = inject(MatSnackBar);
  const isBusy = signal(false);

  const run = async (call: () => Promise<CommandResponse>): Promise<boolean> => {
    if (isBusy()) {
      return false;
    }

    isBusy.set(true);
    let message: string;
    let isAccepted: boolean;

    try {
      const answer = await call();
      message = answer.message;
      isAccepted = true;
    } catch (failure) {
      message = writeFailureText(failure, options.failure);
      isAccepted = false;
    } finally {
      isBusy.set(false);
    }

    snackBar.open(message, 'Close', { duration: WRITE_MESSAGE_MS });
    options.after?.();

    return isAccepted;
  };

  return { isBusy: isBusy.asReadonly(), run };
}

/**
 * The text of a failed write: the runner's own sentence (`ApiError.message`) when it sent one, "not reachable" when
 * no answer came at all, else the plain fallback. Never a status code.
 */
export function writeFailureText(failure: unknown, fallback: string): string {
  if (!(failure instanceof HttpErrorResponse)) {
    return fallback;
  }

  if (failure.status === 0) {
    return 'The runner is not reachable.';
  }

  const apiMessage = (failure.error as { message?: unknown } | null)?.message;
  if (typeof apiMessage === 'string' && apiMessage.length > 0) {
    return apiMessage;
  }

  return fallback;
}
