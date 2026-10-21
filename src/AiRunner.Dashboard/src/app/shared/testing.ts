import { WritableSignal, signal } from '@angular/core';
import { HealthResponse } from '../api/models/health-response';
import { IssueRowResponse } from '../api/models/issue-row-response';
import { JobResponse } from '../api/models/job-response';
import { JobStepResponse } from '../api/models/job-step-response';
import { PullResponse } from '../api/models/pull-response';
import { SetupResponse } from '../api/models/setup-response';
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

/** One call a screen made through `FakeApi`: the generated function and the parameters it got. */
export interface FakeApiCall {
  fn: unknown;
  params: unknown;
}

/**
 * Test-only stand-in for the generated `Api`: `answer(getJob, (params) => job)` sets the reply of one generated
 * function, `calls` records every call with its parameters. A reply that throws becomes a failed call; a function
 * without a reply fails too. Provide it with `{ provide: Api, useValue: fakeApi }`.
 */
export class FakeApi {
  readonly calls: FakeApiCall[] = [];
  private readonly replies = new Map<unknown, (params: never) => unknown>();

  answer<P, R>(
    fn: (http: never, rootUrl: string, params: P) => unknown,
    reply: (params: P) => R,
  ): this {
    this.replies.set(fn, reply as (params: never) => unknown);

    return this;
  }

  callsOf(fn: unknown): unknown[] {
    return this.calls.filter((call) => call.fn === fn).map((call) => call.params);
  }

  invoke(fn: unknown, params?: unknown): Promise<unknown> {
    this.calls.push({ fn, params });

    const reply = this.replies.get(fn);
    if (!reply) {
      return Promise.reject(new Error('FakeApi: no answer for this call.'));
    }

    try {
      return Promise.resolve(reply(params as never));
    } catch (failure) {
      return Promise.reject(failure);
    }
  }
}

/** Lets pending promises (fake API answers) settle, then waits for change detection. */
export async function settle(fixture: { whenStable(): Promise<unknown> }): Promise<void> {
  for (let round = 0; round < 3; round++) {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await fixture.whenStable();
  }
}

/** A finished job; override fields per test. */
export function sampleJob(overrides: Partial<JobResponse> = {}): JobResponse {
  return {
    id: 7,
    type: 'ImplementIssue',
    typeLabel: 'Implement issue',
    status: 'done',
    statusLabel: 'Done',
    repo: 'team/shop',
    number: 42,
    project: 'shop',
    createdAt: '2026-01-01T11:00:00Z',
    startedAt: '2026-01-01T11:01:00Z',
    finishedAt: '2026-01-01T11:04:05Z',
    durationSeconds: 185,
    result: 'PR opened',
    detail: '2 rounds',
    error: null,
    hasLog: true,
    isRunning: false,
    copilotCalls: 2,
    ...overrides,
  };
}

/** An open issue in the ready-simple stage (lane Ready), without flags, badge, action, PRs or sub-issues. */
export function sampleIssue(overrides: Partial<IssueRowResponse> = {}): IssueRowResponse {
  return {
    project: 'shop',
    repo: 'team/shop',
    number: 42,
    title: 'Export orders as CSV',
    state: 'open',
    stateLabel: 'Open',
    stage: 'ready-simple',
    stageLabel: 'Ready - simple',
    lane: 'ready',
    flags: [],
    badge: null,
    waiting: null,
    progress: null,
    pulls: [],
    component: 'api',
    htmlUrl: 'http://gitbucket.local/team/shop/issues/42',
    updatedAt: '2026-01-01T11:30:00Z',
    closedAt: null,
    parent: null,
    subIssues: [],
    actions: [],
    ...overrides,
  };
}

/** An open pull request of the sample issue; override fields per test. */
export function samplePull(overrides: Partial<PullResponse> = {}): PullResponse {
  return {
    repo: 'team/shop',
    number: 50,
    title: 'Export orders as CSV',
    state: 'open',
    stateLabel: 'Open',
    headRef: 'ai/issue-42',
    baseRef: 'main',
    htmlUrl: 'http://gitbucket.local/team/shop/pull/50',
    updatedAt: '2026-01-01T11:40:00Z',
    actions: [],
    ...overrides,
  };
}

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
    isPaused: false,
    isSetupNeeded: false,
    worker: { state: 'idle', stateLabel: 'Idle', since: null },
    errorsLabel: 'No poll errors',
    errors: [],
    ...overrides,
  };
}

/** A runner with a token from the user secrets and no web password; override fields per test. */
export function sampleSetup(overrides: Partial<SetupResponse> = {}): SetupResponse {
  return {
    baseUrl: 'http://gitbucket.local',
    isTokenSet: true,
    tokenSource: 'user-secrets',
    tokenSourceLabel: 'User secrets',
    tokenNote: null,
    login: 'ai-bot',
    canRemoveToken: true,
    isWebPasswordSet: false,
    webPasswordSource: 'none',
    webPasswordSourceLabel: 'Not set',
    webPasswordNote: null,
    canRemoveWebPassword: false,
    secretsFile: '/home/dev/.microsoft/usersecrets/ai-runner/secrets.json',
    ...overrides,
  };
}

/** A finished step; override fields per test. */
export function sampleStep(overrides: Partial<JobStepResponse> = {}): JobStepResponse {
  return {
    seq: 1,
    code: 'prepare',
    label: 'Prepare workspace',
    startedAt: '2026-01-01T11:01:00Z',
    endedAt: '2026-01-01T11:01:12Z',
    durationSeconds: 12,
    state: 'ok',
    stateLabel: 'Done',
    detail: null,
    ...overrides,
  };
}
