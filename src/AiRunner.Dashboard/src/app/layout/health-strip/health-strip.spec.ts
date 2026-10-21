import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { HealthResponse } from '../../api/models/health-response';
import { FakePollResource, fakePollResource, fixedClock, sampleHealth } from '../../shared/testing';
import { HealthStore } from '../health-store';
import { HealthStrip } from './health-strip';

describe('HealthStrip', () => {
  let resource: FakePollResource<HealthResponse>;

  async function render() {
    await TestBed.configureTestingModule({
      imports: [HealthStrip],
      providers: [provideRouter([]), fixedClock, { provide: HealthStore, useValue: { health: resource } }],
    }).compileComponents();

    const fixture = TestBed.createComponent(HealthStrip);
    await fixture.whenStable();

    return fixture;
  }

  function text(element: HTMLElement, testId: string): string | undefined {
    return element.querySelector(`[data-testid="${testId}"]`)?.textContent?.trim();
  }

  it('shows a healthy, idle runner in the success tone', async () => {
    resource = fakePollResource(sampleHealth());

    const fixture = await render();
    const element = fixture.nativeElement as HTMLElement;

    expect(text(element, 'health-state')).toBe('Healthy');
    expect(text(element, 'health-worker')).toContain('Idle');
    expect(element.classList).toContain('tone-success');
    expect(element.querySelector('[data-testid="health-errors"]')).toBeNull();
    expect(element.textContent).toContain('Last poll 30 s ago');
  });

  it('shows the poll errors and the danger tone', async () => {
    resource = fakePollResource(
      sampleHealth({
        state: 'error',
        stateLabel: 'Poll failed in 2 projects',
        errorsLabel: '2 poll errors',
        errors: [
          { project: 'shop', message: 'issue poll failed: timeout', at: '2026-01-01T11:59:30Z' },
          { project: 'crm', message: 'issue poll failed: 401', at: '2026-01-01T11:59:30Z' },
        ],
      }),
    );

    const fixture = await render();
    const element = fixture.nativeElement as HTMLElement;

    expect(text(element, 'health-state')).toBe('Poll failed in 2 projects');
    expect(text(element, 'health-errors')).toContain('2 poll errors');
    expect(element.classList).toContain('tone-danger');
  });

  it('shows a busy worker', async () => {
    resource = fakePollResource(
      sampleHealth({ worker: { state: 'busy', stateLabel: 'Running a job', since: '2026-01-01T11:58:00Z' } }),
    );

    const fixture = await render();

    expect(text(fixture.nativeElement, 'health-worker')).toContain('Running a job');
    expect(text(fixture.nativeElement, 'health-worker')).toContain('2 min ago');
  });

  it('says the runner is not reachable when the call fails, even with older data', async () => {
    resource = fakePollResource(sampleHealth());
    resource.error.set('The runner is not reachable.');

    const fixture = await render();
    const element = fixture.nativeElement as HTMLElement;

    expect(text(element, 'health-state')).toBe('Runner not reachable');
    expect(element.classList).toContain('tone-danger');
  });

  it('links the state to the Setup screen while the runner has no token', async () => {
    resource = fakePollResource(
      sampleHealth({ state: 'needs-setup', stateLabel: 'Needs setup: no GitBucket token', isSetupNeeded: true }),
    );

    const fixture = await render();
    const element = fixture.nativeElement as HTMLElement;
    const state = element.querySelector('[data-testid="health-state"]');

    expect(state?.tagName).toBe('A');
    expect(state?.getAttribute('href')).toBe('/setup');
    expect(state?.textContent?.trim()).toBe('Needs setup: no GitBucket token');
    expect(element.classList).toContain('tone-warning');
  });

  it('shows the state as plain text when no setup is needed', async () => {
    resource = fakePollResource(sampleHealth());

    const fixture = await render();
    const state = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="health-state"]');

    expect(state?.tagName).toBe('STRONG');
  });

  it('uses a neutral tone for a state code it does not know', async () => {
    resource = fakePollResource(sampleHealth({ state: 'something-new', stateLabel: 'Something new' }));

    const fixture = await render();

    expect((fixture.nativeElement as HTMLElement).classList).toContain('tone-neutral');
    expect(text(fixture.nativeElement, 'health-state')).toBe('Something new');
  });
});

describe('HealthStrip with the real store', () => {
  it('reads GET /api/health through the generated client', async () => {
    await TestBed.configureTestingModule({
      imports: [HealthStrip],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), fixedClock],
    }).compileComponents();

    const fixture = TestBed.createComponent(HealthStrip);
    const http = TestBed.inject(HttpTestingController);

    http.expectOne({ method: 'GET', url: '/api/health' }).flush(sampleHealth({ stateLabel: 'Healthy' }));
    await fixture.whenStable();

    const state = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="health-state"]');
    expect(state?.textContent?.trim()).toBe('Healthy');
    http.verify();
  });
});
