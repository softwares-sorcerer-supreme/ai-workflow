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

const IDLE: OverviewResponse = { runningJob: null, queueLabel: 'Queue is empty', queue: [] };

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
