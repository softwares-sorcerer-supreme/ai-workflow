import { HttpErrorResponse } from '@angular/common/http';
import { Signal, inject, signal } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { CommandResponse } from '../api/models/command-response';
import { describeError } from './poll-resource';

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
      message = describeError(failure, options.failure);
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
 * Why the runner refused a save of an editor screen (Agents, Settings): `conflict` the file changed since it was read
 * (409, the person reloads), `invalid` the runner's checks refused the text (400). `body` is the answer as sent, for a
 * screen that shows more of it (the reasons of an agent file).
 */
export interface SaveRefusal {
  kind: 'conflict' | 'invalid';
  message: string;
  body: unknown;
}

/** The refusal in a failed save, or undefined when it failed another way (the snack bar already says so). */
export function saveRefusal(failure: unknown, fallback: string): SaveRefusal | undefined {
  if (!(failure instanceof HttpErrorResponse)) {
    return undefined;
  }

  const message = describeError(failure, fallback);

  if (failure.status === 409) {
    return { kind: 'conflict', message, body: failure.error };
  }

  if (failure.status === 400) {
    return { kind: 'invalid', message, body: failure.error };
  }

  return undefined;
}
