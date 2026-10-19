import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Api } from '../../api/api';
import { getOverview } from '../../api/fn/overview/get-overview';
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
    expect(element.textContent).toContain('5 min 00 s');
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
