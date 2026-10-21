import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { CommandResponse } from '../api/models/command-response';
import { describeError } from './poll-resource';
import { writeAction } from './write-action';

describe('writeAction', () => {
  let messages: string[];

  function create(after?: () => void) {
    messages = [];
    TestBed.configureTestingModule({
      providers: [
        { provide: MatSnackBar, useValue: { open: (message: string) => messages.push(message) } },
      ],
    });

    return TestBed.runInInjectionContext(() => writeAction({ failure: 'Could not do it.', after }));
  }

  it('is busy while the call runs, shows the message, then runs after', async () => {
    let afterCalls = 0;
    const action = create(() => afterCalls++);
    let resolve!: (answer: CommandResponse) => void;

    const running = action.run(() => new Promise((done) => (resolve = done)));

    expect(action.isBusy()).toBe(true);
    expect(afterCalls).toBe(0);

    resolve({ message: 'Done.' });

    expect(await running).toBe(true);
    expect(action.isBusy()).toBe(false);
    expect(messages).toEqual(['Done.']);
    expect(afterCalls).toBe(1);
  });

  it('ignores a second click while the first call runs', async () => {
    const action = create();
    let resolve!: (answer: CommandResponse) => void;
    let calls = 0;
    const call = () => {
      calls++;

      return new Promise<CommandResponse>((done) => (resolve = done));
    };

    const first = action.run(call);
    const second = await action.run(call);
    resolve({ message: 'Done.' });
    await first;

    expect(second).toBe(false);
    expect(calls).toBe(1);
  });

  it('shows the failure text and still runs after when the call fails', async () => {
    let afterCalls = 0;
    const action = create(() => afterCalls++);

    const isAccepted = await action.run(() =>
      Promise.reject(
        new HttpErrorResponse({ status: 409, error: { message: 'Job 9 is not running.' } }),
      ),
    );

    expect(isAccepted).toBe(false);
    expect(messages).toEqual(['Job 9 is not running.']);
    expect(afterCalls).toBe(1);
  });
});

describe('describeError with a write fallback', () => {
  it('uses the runner message of an ApiError', () => {
    const failure = new HttpErrorResponse({
      status: 403,
      error: { message: 'This write was refused.' },
    });

    expect(describeError(failure, 'fallback')).toBe('This write was refused.');
  });

  it('says the runner is not reachable when no answer came', () => {
    expect(describeError(new HttpErrorResponse({ status: 0 }), 'fallback')).toBe(
      'The runner is not reachable.',
    );
  });

  it('falls back to the plain sentence without a message or for any other failure', () => {
    const failure = new HttpErrorResponse({
      status: 500,
      statusText: 'Server Error',
      error: 'boom',
    });

    expect(describeError(failure, 'Could not do it.')).toBe('Could not do it.');
    expect(describeError(new Error('x'), 'Could not do it.')).toBe('Could not do it.');
  });
});
