import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  EnvironmentInjector,
  createEnvironmentInjector,
  runInInjectionContext,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { describeError, pollResource } from './poll-resource';

describe('pollResource', () => {
  let hidden = false;

  beforeEach(() => {
    vi.useFakeTimers();
    hidden = false;
    Object.defineProperty(TestBed.inject(DOCUMENT), 'hidden', {
      configurable: true,
      get: () => hidden,
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function start(load: () => Promise<number>) {
    const injector = createEnvironmentInjector([], TestBed.inject(EnvironmentInjector));
    const resource = runInInjectionContext(injector, () => pollResource(load, 1000));

    return { resource, injector };
  }

  function setVisibility(isHidden: boolean) {
    hidden = isHidden;
    TestBed.inject(DOCUMENT).dispatchEvent(new Event('visibilitychange'));
  }

  it('loads at once and then every interval', async () => {
    let calls = 0;
    const { resource } = start(async () => ++calls);

    await vi.advanceTimersByTimeAsync(0);
    expect(resource.value()).toBe(1);
    expect(resource.isLoading()).toBe(false);

    await vi.advanceTimersByTimeAsync(1000);
    expect(resource.value()).toBe(2);
  });

  it('keeps the last value when a call fails and clears the error on the next success', async () => {
    let calls = 0;
    const { resource } = start(async () => {
      calls++;
      if (calls === 2) {
        throw new HttpErrorResponse({ status: 0 });
      }

      return calls;
    });

    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(1000);
    expect(resource.value()).toBe(1);
    expect(resource.error()).toBe('The runner is not reachable.');

    await vi.advanceTimersByTimeAsync(1000);
    expect(resource.value()).toBe(3);
    expect(resource.error()).toBeUndefined();
  });

  it('pauses while the tab is hidden and loads at once when it is visible again', async () => {
    let calls = 0;
    const { resource } = start(async () => ++calls);
    await vi.advanceTimersByTimeAsync(0);

    setVisibility(true);
    await vi.advanceTimersByTimeAsync(5000);
    expect(calls).toBe(1);

    setVisibility(false);
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toBe(2);
    expect(resource.value()).toBe(2);
  });

  it('loads once more right after the running call when reload is asked during it', async () => {
    const answers: Array<(value: number) => void> = [];
    let calls = 0;
    const { resource } = start(
      () =>
        new Promise<number>((resolve) => {
          calls++;
          answers.push(resolve);
        }),
    );

    resource.reload();
    expect(calls).toBe(1);

    answers[0](1);
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toBe(2);

    answers[1](2);
    await vi.advanceTimersByTimeAsync(0);
    expect(resource.value()).toBe(2);
  });

  it('stops when its owner is destroyed', async () => {
    let calls = 0;
    const { injector } = start(async () => ++calls);
    await vi.advanceTimersByTimeAsync(0);

    injector.destroy();
    await vi.advanceTimersByTimeAsync(5000);

    expect(calls).toBe(1);
  });
});

describe('describeError', () => {
  it('names an HTTP status', () => {
    const error = new HttpErrorResponse({ status: 500, statusText: 'Internal Server Error' });

    expect(describeError(error)).toBe('The runner answered 500 Internal Server Error');
  });

  it('uses the sentence of an API error', () => {
    const error = new HttpErrorResponse({
      status: 400,
      statusText: 'Bad Request',
      error: { message: "Unknown status 'x'." },
    });

    expect(describeError(error)).toBe("Unknown status 'x'.");
  });
});
