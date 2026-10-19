import { TestBed } from '@angular/core/testing';
import { HealthResponse } from '../../api/models/health-response';
import { HealthStore } from '../../layout/health-store';
import { FakePollResource, fakePollResource, fixedClock, sampleHealth } from '../../shared/testing';
import { Overview } from './overview';

describe('Overview', () => {
  async function render(resource: FakePollResource<HealthResponse>) {
    await TestBed.configureTestingModule({
      imports: [Overview],
      providers: [fixedClock, { provide: HealthStore, useValue: { health: resource } }],
    }).compileComponents();

    const fixture = TestBed.createComponent(Overview);
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  it('shows the runner facts and "no poll errors"', async () => {
    const element = await render(fakePollResource(sampleHealth()));

    expect(element.textContent).toContain('laptop-1');
    expect(element.textContent).toContain('C:\\runner\\repos.yml');
    expect(element.textContent).toContain('every 1 min 30 s');
    expect(element.textContent).toContain('No poll errors');
  });

  it('lists the poll errors per project', async () => {
    const health = sampleHealth({
      errors: [{ project: 'shop', message: 'issue poll failed: timeout', at: '2026-01-01T11:59:30Z' }],
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
