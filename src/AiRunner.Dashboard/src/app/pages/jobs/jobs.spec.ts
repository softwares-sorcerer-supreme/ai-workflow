import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { MatPaginatorHarness } from '@angular/material/paginator/testing';
import { MatSelectHarness } from '@angular/material/select/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { Api } from '../../api/api';
import { getJobs } from '../../api/fn/jobs/get-jobs';
import { JobsPageResponse } from '../../api/models/jobs-page-response';
import { FakeApi, fixedClock, sampleJob, settle } from '../../shared/testing';
import { Jobs } from './jobs';

describe('Jobs', () => {
  function page(overrides: Partial<JobsPageResponse> = {}): JobsPageResponse {
    return {
      items: [
        sampleJob(),
        sampleJob({
          id: 8,
          status: 'failed',
          statusLabel: 'Failed',
          result: null,
          error: 'build failed: 3 errors',
        }),
      ],
      page: 1,
      pageSize: 25,
      total: 60,
      totalPages: 3,
      ...overrides,
    };
  }

  async function render(reply: JobsPageResponse = page()) {
    const api = new FakeApi().answer(getJobs, () => reply);

    TestBed.configureTestingModule({
      imports: [Jobs],
      providers: [provideRouter([]), fixedClock, { provide: Api, useValue: api }],
    });

    const fixture = TestBed.createComponent(Jobs);
    await settle(fixture);

    const loader = TestbedHarnessEnvironment.loader(fixture);

    return { api, fixture, loader, element: fixture.nativeElement as HTMLElement };
  }

  it('lists the jobs with status chip, issue, duration and the error of a failed job', async () => {
    const { element } = await render();

    const rows = element.querySelectorAll('tr.mat-mdc-row');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('team/shop#42');
    expect(rows[0].textContent).toContain('3 min 05 s');
    expect(rows[0].querySelector('app-status-chip')?.classList).toContain('tone-success');
    expect(rows[1].querySelector('app-status-chip')?.classList).toContain('tone-danger');
    expect(rows[1].querySelector('.error-text')?.textContent).toBe('build failed: 3 errors');
  });

  it('asks the API again for page 1 with the chosen status', async () => {
    const { api, fixture, loader } = await render();

    const [statusSelect] = await loader.getAllHarnesses(MatSelectHarness);
    await statusSelect.open();
    await statusSelect.clickOptions({ text: 'Failed' });
    await settle(fixture);

    expect(api.callsOf(getJobs).at(-1)).toEqual({ status: 'failed', type: undefined, page: 1 });
  });

  it('pages through the API', async () => {
    const { api, fixture, loader } = await render();

    const paginator = await loader.getHarness(MatPaginatorHarness);
    expect(await paginator.getRangeLabel()).toBe('1 – 25 of 60');

    await paginator.goToNextPage();
    await settle(fixture);

    expect(api.callsOf(getJobs).at(-1)).toEqual({ status: undefined, type: undefined, page: 2 });
  });

  it('opens a job when its row is clicked', async () => {
    const { element } = await render();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    (element.querySelectorAll('tr.mat-mdc-row')[1] as HTMLElement).click();

    expect(navigate).toHaveBeenCalledWith(['/jobs', 8]);
  });

  it('says when no job matches', async () => {
    const { element } = await render(page({ items: [], total: 0, totalPages: 0 }));

    expect(element.textContent).toContain('No jobs found');
  });
});
