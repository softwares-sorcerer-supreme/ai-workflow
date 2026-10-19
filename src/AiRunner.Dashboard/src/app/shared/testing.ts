import { WritableSignal, signal } from '@angular/core';
import { HealthResponse } from '../api/models/health-response';
import { PollResource } from './poll-resource';
import { Clock } from './time/clock';

/** Test-only: a poll resource whose signals the test sets by hand. */
export interface FakePollResource<T> extends PollResource<T> {
  readonly value: WritableSignal<T | undefined>;
  readonly error: WritableSignal<string | undefined>;
  readonly isLoading: WritableSignal<boolean>;
  reloads: number;
}

export function fakePollResource<T>(value?: T): FakePollResource<T> {
  const resource: FakePollResource<T> = {
    value: signal<T | undefined>(value),
    error: signal<string | undefined>(undefined),
    isLoading: signal(value === undefined),
    updatedAt: signal<Date | undefined>(undefined),
    reloads: 0,
    reload: () => {
      resource.reloads++;
    },
  };

  return resource;
}

/** 2026-01-01 12:00:00 UTC: the "now" of every test that shows times. */
export const TEST_NOW = Date.UTC(2026, 0, 1, 12, 0, 0);

/** A clock that does not tick. */
export const fixedClock = { provide: Clock, useValue: { now: signal(TEST_NOW).asReadonly() } };

/** A healthy runner; override fields per test. */
export function sampleHealth(overrides: Partial<HealthResponse> = {}): HealthResponse {
  return {
    state: 'ok',
    stateLabel: 'Healthy',
    runnerName: 'laptop-1',
    startedAt: '2026-01-01T11:00:00Z',
    configPath: 'C:\\runner\\repos.yml',
    copilotVersion: '1.0.0',
    pollIntervalSeconds: 90,
    lastPollStartedAt: '2026-01-01T11:59:00Z',
    lastPollEndedAt: '2026-01-01T11:59:30Z',
    isPolling: false,
    worker: { state: 'idle', stateLabel: 'Idle', since: null },
    errorsLabel: 'No poll errors',
    errors: [],
    ...overrides,
  };
}
