import { HarnessLoader } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { MatButtonHarness } from '@angular/material/button/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialogHarness } from '@angular/material/dialog/testing';
import { MatSnackBarHarness } from '@angular/material/snack-bar/testing';
import { provideRouter } from '@angular/router';
import { Api } from '../../api/api';
import { cancelJob } from '../../api/fn/jobs/cancel-job';
import { getOverview } from '../../api/fn/overview/get-overview';
import { pauseRunner } from '../../api/fn/runner/pause-runner';
import { pollNow } from '../../api/fn/runner/poll-now';
import { resumeRunner } from '../../api/fn/runner/resume-runner';
import { CommandResponse } from '../../api/models/command-response';
import { HealthResponse } from '../../api/models/health-response';
import { OverviewResponse } from '../../api/models/overview-response';
import { HealthStore } from '../../layout/health-store';
import {
  FakeApi,
  FakePollResource,
  fakePollResource,
  fixedClock,
  sampleHealth,
  sampleJob,
  sampleStep,
  settle,
} from '../../shared/testing';
import { Overview } from './overview';

const IDLE: OverviewResponse = {
  runningJob: null,
  queueLabel: 'Queue is empty',
  queue: [],
  waitingLabel: 'Nothing is waiting on you',
  waitingOn: [],
};

describe('Overview', () => {
  async function render(
    resource: FakePollResource<HealthResponse>,
    overview: OverviewResponse = IDLE,
  ) {
    await TestBed.configureTestingModule({
      imports: [Overview],
      providers: [
        provideRouter([]),
        fixedClock,
        { provide: HealthStore, useValue: { health: resource } },
        { provide: Api, useValue: new FakeApi().answer(getOverview, () => overview) },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(Overview);
    await settle(fixture);

    return fixture.nativeElement as HTMLElement;
  }

  it('says nothing is running and the queue is empty when the worker is idle', async () => {
    const element = await render(fakePollResource(sampleHealth()));

    expect(element.textContent).toContain('Nothing is running');
    expect(element.querySelector('#queue')?.textContent).toContain('Queue is empty');
  });

  it('shows the running job with its round and the queue', async () => {
    const element = await render(fakePollResource(sampleHealth()), {
      ...IDLE,
      runningJob: {
        id: 9,
        type: 'ImplementIssue',
        typeLabel: 'Implement issue',
        repo: 'team/shop',
        number: 42,
        project: 'shop',
        startedAt: '2026-01-01T11:55:00Z',
        elapsedSeconds: 300,
        round: 2,
        roundLabel: 'Round 2',
        currentStep: { code: 'copilot', label: 'Copilot round 2' },
        steps: [
          sampleStep({ seq: 1 }),
          sampleStep({
            seq: 2,
            code: 'copilot',
            label: 'Copilot round 2',
            state: 'running',
            stateLabel: 'Running',
            endedAt: null,
            durationSeconds: null,
          }),
        ],
      },
      queueLabel: '1 job queued',
      queue: [
        sampleJob({
          id: 10,
          type: 'AnalyzeIssue',
          typeLabel: 'Analyze issue',
          status: 'pending',
          statusLabel: 'Queued',
          number: 43,
        }),
      ],
    });

    expect(element.textContent).toContain('Implement issue');
    expect(element.textContent).toContain('team/shop#42');
    expect(element.textContent).toContain('Round 2');
    expect(element.querySelector('[data-testid="current-step"]')?.textContent).toBe('Copilot round 2');
    expect(element.textContent).toContain('5 min 00 s');

    const steps = [...element.querySelectorAll('app-job-steps li.step')];
    expect(steps.length).toBe(2);
    expect(steps[0].textContent).toContain('Prepare workspace');
    expect(steps[1].textContent).toContain('Running');
    expect(steps[1].classList).toContain('tone-progress');
    expect(element.querySelector('a[href="/jobs/9"]')).not.toBeNull();

    const queue = element.querySelector('#queue') as HTMLElement;
    expect(queue.textContent).toContain('1 job queued');
    expect(queue.querySelector('tr.mat-mdc-row')?.textContent).toContain('team/shop#43');
    expect(queue.querySelector('app-status-chip')?.classList).toContain('tone-progress');
  });

  it('shows the empty text of the waiting panel from the API', async () => {
    const element = await render(fakePollResource(sampleHealth()));

    const waiting = element.querySelector('#waiting') as HTMLElement;
    expect(waiting.querySelector('h2')?.textContent).toContain('Nothing is waiting on you');
    expect(waiting.querySelector('tr.mat-mdc-row')).toBeNull();
  });

  it('lists what waits on you with the action text and a link to the issue detail', async () => {
    const element = await render(fakePollResource(sampleHealth()), {
      ...IDLE,
      waitingLabel: '2 waiting on you',
      waitingOn: [
        {
          project: 'shop',
          repo: 'team/shop',
          number: 42,
          title: 'Export orders as CSV',
          stage: { code: 'questions-open', label: 'Questions open' },
          flags: [],
          action: { code: 'answer-questions', label: 'Answer the questions' },
          htmlUrl: 'http://gitbucket.local/team/shop/issues/42',
          updatedAt: '2026-01-01T10:00:00Z',
        },
        {
          project: 'shop',
          repo: 'team/shop',
          number: 43,
          title: 'Add filters',
          stage: { code: 'pr-open', label: 'PR open' },
          flags: [],
          action: { code: 'review-and-merge', label: 'Review and merge the PR' },
          htmlUrl: null,
          updatedAt: '2026-01-01T11:00:00Z',
        },
      ],
    });

    const waiting = element.querySelector('#waiting') as HTMLElement;
    expect(waiting.querySelector('h2')?.textContent).toContain('2 waiting on you');

    const rows = Array.from(waiting.querySelectorAll('tr.mat-mdc-row'));
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('Answer the questions');
    expect(rows[0].textContent).toContain('#42 team/shop');
    expect(rows[0].querySelector('app-stage-chip app-status-chip')?.classList).toContain('tone-warning');
    expect(rows[0].querySelector('a')?.getAttribute('href')).toBe('/issues/team/shop/42');
    expect(rows[1].textContent).toContain('Review and merge the PR');
  });

  it('shows the runner facts and "no poll errors"', async () => {
    const element = await render(fakePollResource(sampleHealth()));

    expect(element.textContent).toContain('laptop-1');
    expect(element.textContent).toContain('C:\\runner\\repos.yml');
    expect(element.textContent).toContain('every 1 min 30 s');
    expect(element.textContent).toContain('No poll errors');
  });

  it('lists the poll errors per project', async () => {
    const health = sampleHealth({
      errors: [
        { project: 'shop', message: 'issue poll failed: timeout', at: '2026-01-01T11:59:30Z' },
      ],
    });

    const element = await render(fakePollResource(health));

    const row = element.querySelector('#poll-errors tr.mat-mdc-row');
    expect(row?.textContent).toContain('shop');
    expect(row?.textContent).toContain('issue poll failed: timeout');
  });

  it('offers a retry when the runner cannot be reached', async () => {
    const resource = fakePollResource<HealthResponse>();
    resource.isLoading.set(false);
    resource.error.set('The runner is not reachable.');

    const element = await render(resource);
    (element.querySelector('app-view-state button') as HTMLButtonElement).click();

    expect(element.textContent).toContain('The runner is not reachable');
    expect(resource.reloads).toBe(1);
  });
});

describe('Overview writes', () => {
  const RUNNING: OverviewResponse = {
    ...IDLE,
    runningJob: {
      id: 9,
      type: 'ImplementIssue',
      typeLabel: 'Implement issue',
      repo: 'team/shop',
      number: 42,
      project: 'shop',
      startedAt: '2026-01-01T11:55:00Z',
      elapsedSeconds: 300,
      round: null,
      roundLabel: null,
      currentStep: null,
      steps: [],
    },
  };

  let api: FakeApi;
  let health: FakePollResource<HealthResponse>;

  async function render(overview: OverviewResponse = IDLE) {
    await TestBed.configureTestingModule({
      imports: [Overview],
      providers: [
        provideRouter([]),
        fixedClock,
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: HealthStore, useValue: { health } },
        { provide: Api, useValue: api.answer(getOverview, () => overview) },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(Overview);
    await settle(fixture);

    return {
      fixture,
      loader: TestbedHarnessEnvironment.loader(fixture),
      root: TestbedHarnessEnvironment.documentRootLoader(fixture),
    };
  }

  function button(loader: HarnessLoader, testId: string): Promise<MatButtonHarness> {
    return loader.getHarness(MatButtonHarness.with({ selector: `[data-testid="${testId}"]` }));
  }

  async function snackText(root: HarnessLoader): Promise<string> {
    const snackBar = await root.getHarness(MatSnackBarHarness);

    return snackBar.getMessage();
  }

  async function confirmCancel(root: HarnessLoader, label: string): Promise<void> {
    const dialog = await root.getHarness(MatDialogHarness);
    const choice = await dialog.getHarness(MatButtonHarness.with({ text: label }));
    await choice.click();
  }

  /** A reply that stays open until the test calls `resolve`. */
  function deferred<T>() {
    let resolve!: (value: T) => void;
    const promise = new Promise<T>((done) => (resolve = done));

    return { promise, resolve };
  }

  beforeEach(() => {
    api = new FakeApi();
    health = fakePollResource(sampleHealth());
  });

  it('starts a poll, shows the busy state, then the runner message, and reloads the health', async () => {
    const reply = deferred<CommandResponse>();
    api.answer(pollNow, () => reply.promise);
    const { fixture, loader, root } = await render();

    const pollButton = await button(loader, 'poll-now');
    await pollButton.click();

    expect(api.callsOf(pollNow).length).toBe(1);
    expect(await pollButton.isDisabled()).toBe(true);
    const spinner = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-testid="poll-now"] mat-progress-spinner',
    );
    expect(spinner).not.toBeNull();

    reply.resolve({ message: 'A poll starts now.' });
    await settle(fixture);

    expect(await pollButton.isDisabled()).toBe(false);
    expect(await snackText(root)).toBe('A poll starts now.');
    expect(health.reloads).toBe(1);
  });

  it('offers Pause while running; the label changes only with the next health answer', async () => {
    api.answer(pauseRunner, () => ({ message: 'Paused. No new job starts.' }));
    const { fixture, loader, root } = await render();

    const pauseButton = await button(loader, 'pause-resume');
    expect(await pauseButton.getText()).toContain('Pause');

    await pauseButton.click();
    await settle(fixture);

    expect(api.callsOf(pauseRunner).length).toBe(1);
    expect(api.callsOf(resumeRunner).length).toBe(0);
    expect(await snackText(root)).toBe('Paused. No new job starts.');
    expect(await pauseButton.getText()).toContain('Pause');
    expect(health.reloads).toBe(1);

    health.value.set(sampleHealth({ isPaused: true, state: 'paused', stateLabel: 'Paused: no new jobs' }));
    await settle(fixture);

    expect(await pauseButton.getText()).toContain('Resume');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Paused: no new jobs');
  });

  it('offers Resume while paused and calls resume', async () => {
    health = fakePollResource(sampleHealth({ isPaused: true, state: 'paused', stateLabel: 'Paused: no new jobs' }));
    api.answer(resumeRunner, () => ({ message: 'Resumed.' }));
    const { fixture, loader, root } = await render();

    const resumeButton = await button(loader, 'pause-resume');
    expect(await resumeButton.getText()).toContain('Resume');

    await resumeButton.click();
    await settle(fixture);

    expect(api.callsOf(resumeRunner).length).toBe(1);
    expect(api.callsOf(pauseRunner).length).toBe(0);
    expect(await snackText(root)).toBe('Resumed.');
  });

  it('shows the message of a refused write, and a plain sentence when the runner sent none', async () => {
    api.answer(pollNow, () => {
      throw new HttpErrorResponse({ status: 403, error: { message: 'This write was refused.' } });
    });
    api.answer(pauseRunner, () => {
      throw new HttpErrorResponse({ status: 500, statusText: 'Server Error' });
    });
    const { fixture, loader, root } = await render();

    const pollButton = await button(loader, 'poll-now');
    await pollButton.click();
    await settle(fixture);
    expect(await snackText(root)).toBe('This write was refused.');

    const pauseButton = await button(loader, 'pause-resume');
    await pauseButton.click();
    await settle(fixture);
    expect(await snackText(root)).toBe('The runner did not change its pause state.');
  });

  it('asks before it cancels; "Keep running" makes no call', async () => {
    const { fixture, loader, root } = await render(RUNNING);

    const cancelButton = await button(loader, 'cancel-job');
    await cancelButton.click();
    const dialog = await root.getHarness(MatDialogHarness);

    expect(await dialog.getRole()).toBe('alertdialog');
    expect(await dialog.getTitleText()).toBe('Cancel the running job?');
    expect(await dialog.getContentText()).toContain('failure comment');
    expect(await dialog.getContentText()).toContain('says how to run it again');

    await confirmCancel(root, 'Keep running');
    await settle(fixture);

    expect(await root.getAllHarnesses(MatDialogHarness)).toEqual([]);
    expect(api.callsOf(cancelJob)).toEqual([]);
  });

  it('cancels the running job by its id after the confirmation and reloads the overview', async () => {
    api.answer(cancelJob, () => ({ message: 'Job 9 is being cancelled.' }));
    const { fixture, loader, root } = await render(RUNNING);
    const overviewCallsBefore = api.callsOf(getOverview).length;

    const cancelButton = await button(loader, 'cancel-job');
    await cancelButton.click();
    await confirmCancel(root, 'Cancel job');
    await settle(fixture);

    expect(api.callsOf(cancelJob)).toEqual([{ id: 9 }]);
    expect(await snackText(root)).toBe('Job 9 is being cancelled.');
    expect(api.callsOf(getOverview).length).toBeGreaterThan(overviewCallsBefore);
    expect(health.reloads).toBe(1);
  });

  it('shows the server message when the job is no longer running (409)', async () => {
    api.answer(cancelJob, () => {
      throw new HttpErrorResponse({ status: 409, error: { message: 'Job 9 is not running.' } });
    });
    const { fixture, loader, root } = await render(RUNNING);

    const cancelButton = await button(loader, 'cancel-job');
    await cancelButton.click();
    await confirmCancel(root, 'Cancel job');
    await settle(fixture);

    expect(api.callsOf(cancelJob)).toEqual([{ id: 9 }]);
    expect(await snackText(root)).toBe('Job 9 is not running.');
  });
});
