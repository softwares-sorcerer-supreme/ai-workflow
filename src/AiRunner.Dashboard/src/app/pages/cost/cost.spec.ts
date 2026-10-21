import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { MatButtonToggleHarness } from '@angular/material/button-toggle/testing';
import { Api } from '../../api/api';
import { GetCost$Params, getCost } from '../../api/fn/cost/get-cost';
import { CostResponse } from '../../api/models/cost-response';
import { CostRowResponse } from '../../api/models/cost-row-response';
import { FakeApi, settle } from '../../shared/testing';
import { Cost } from './cost';

function row(overrides: Partial<CostRowResponse> = {}): CostRowResponse {
  return {
    key: '2026-01-01',
    label: '2026-01-01',
    calls: 4,
    jobs: 3,
    jobsWithoutCount: 0,
    jobsWithoutUsage: 0,
    durationSeconds: 185,
    nanoAiu: 1500,
    inputTokens: 200,
    cacheReadTokens: 300,
    cacheWriteTokens: 40,
    outputTokens: 50,
    ...overrides,
  };
}

function answer(overrides: Partial<CostResponse> = {}): CostResponse {
  return {
    by: 'day',
    byLabel: 'By day',
    from: null,
    to: null,
    rows: [
      row(),
      row({ key: '2026-01-02', label: '2026-01-02', calls: 2, jobs: 1, durationSeconds: 60, nanoAiu: 700 }),
    ],
    // Deliberately not the sum of the rows: the screen must show the API's total, never add up itself.
    total: row({
      key: 'total',
      label: 'Total',
      calls: 99,
      jobs: 42,
      durationSeconds: 3600,
      nanoAiu: 1234567890,
      inputTokens: 1000000,
      cacheReadTokens: 2500000,
      cacheWriteTokens: 12345,
      outputTokens: 67890,
    }),
    note: null,
    ...overrides,
  };
}

describe('Cost', () => {
  async function render(reply: (params: GetCost$Params) => CostResponse = () => answer()) {
    const api = new FakeApi().answer(getCost, reply);

    TestBed.configureTestingModule({
      imports: [Cost],
      providers: [{ provide: Api, useValue: api }],
    });

    const fixture = TestBed.createComponent(Cost);
    await settle(fixture);

    const loader = TestbedHarnessEnvironment.loader(fixture);

    return { api, fixture, loader, element: fixture.nativeElement as HTMLElement };
  }

  it('asks for the day grouping first, without a range', async () => {
    const { api } = await render();

    expect(api.callsOf(getCost).at(0)).toEqual({ by: 'day', from: undefined, to: undefined });
  });

  it('lists the rows and then the total the API sent', async () => {
    const { element } = await render();

    const rows = Array.from(element.querySelectorAll('[aria-labelledby="table-title"] tr.mat-mdc-row'));
    expect(rows.length).toBe(3);
    expect(rows[0].textContent).toContain('2026-01-01');
    expect(rows[0].textContent).toContain('3 min 05 s');
    expect(rows[2].textContent).toContain('Total');
    expect(rows[2].querySelector('.mat-column-calls')?.textContent?.trim()).toBe('99');
    expect(rows[2].querySelector('.mat-column-jobs')?.textContent?.trim()).toBe('42');
    expect(rows[2].querySelector('.total')).not.toBeNull();
  });

  it('shows billed credits and tokens with thousands separators, the total line in the total style', async () => {
    const { element } = await render();

    const headers = Array.from(element.querySelectorAll('th')).map((header) => header.textContent?.trim());
    expect(headers).toEqual(
      expect.arrayContaining(['Billed (nano AIU)', 'Input', 'Cache read', 'Cache write', 'Output']),
    );

    const total = Array.from(element.querySelectorAll('tr.mat-mdc-row')).at(-1) as Element;
    const cell = (column: string) => total.querySelector(`.mat-column-${column}`);
    expect(cell('nanoAiu')?.textContent?.trim()).toBe('1,234,567,890');
    expect(cell('inputTokens')?.textContent?.trim()).toBe('1,000,000');
    expect(cell('cacheReadTokens')?.textContent?.trim()).toBe('2,500,000');
    expect(cell('cacheWriteTokens')?.textContent?.trim()).toBe('12,345');
    expect(cell('outputTokens')?.textContent?.trim()).toBe('67,890');
    expect(cell('nanoAiu')?.querySelector('.total')).not.toBeNull();
  });

  it('shows the note as sent', async () => {
    const note = '2 jobs have no usage (older runs or an older Copilot CLI), so billed and tokens may be higher.';
    const { element } = await render(() => answer({ note }));

    expect(element.querySelector('app-inline-note')?.textContent).toContain(note);
  });

  it('shows no note when the API sends none', async () => {
    const { element } = await render();

    expect(element.querySelector('app-inline-note')).toBeNull();
  });

  it('draws one chart of the billed value: columns for days', async () => {
    const { element } = await render();

    expect(element.querySelector('#chart-title')?.textContent?.trim()).toBe('Billed (nano AIU)');
    expect(element.querySelectorAll('app-cost-chart').length).toBe(1);
    expect(element.querySelector('app-cost-chart title')?.textContent).toBe('Billed (nano AIU) - By day');
    expect(element.querySelectorAll('app-cost-chart path.bar').length).toBe(2);
    expect(element.querySelector('app-cost-chart .label')).toBeNull();
  });

  it('asks for the chosen grouping and draws labelled bars for it', async () => {
    const { api, fixture, loader } = await render((params) =>
      params.by === 'project'
        ? answer({
            by: 'project',
            byLabel: 'By project',
            rows: [row({ key: 'shop', label: 'shop', calls: 7, nanoAiu: 4200 })],
          })
        : answer(),
    );

    const toggle = await loader.getHarness(MatButtonToggleHarness.with({ text: 'Project' }));
    await toggle.check();
    await settle(fixture);

    expect(api.callsOf(getCost).at(-1)).toEqual({ by: 'project', from: undefined, to: undefined });

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('app-cost-chart .label')?.textContent).toContain('shop');
    expect(element.querySelector('app-cost-chart .value')?.textContent?.trim()).toBe('4,200');
  });

  it('offers the model grouping and shows the model rows as the API labels them', async () => {
    const { api, fixture, loader } = await render((params) =>
      params.by === 'model'
        ? answer({
            by: 'model',
            byLabel: 'By model and agent',
            rows: [row({ key: 'claude-sonnet-5.5 - code-reviewer', label: 'claude-sonnet-5.5 - code-reviewer' })],
          })
        : answer(),
    );

    const toggle = await loader.getHarness(MatButtonToggleHarness.with({ text: 'Model' }));
    await toggle.check();
    await settle(fixture);

    expect(api.callsOf(getCost).at(-1)).toEqual({ by: 'model', from: undefined, to: undefined });

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('app-cost-chart .label')?.textContent).toContain('claude-sonnet-5.5 - code-reviewer');
    const firstRow = element.querySelector('[aria-labelledby="table-title"] tr.mat-mdc-row');
    expect(firstRow?.textContent).toContain('claude-sonnet-5.5 - code-reviewer');
  });

  it('passes the chosen days to the API', async () => {
    const { api, fixture, element } = await render();

    const [fromInput, toInput] = Array.from(element.querySelectorAll('input[type="date"]')) as HTMLInputElement[];
    fromInput.value = '2026-01-01';
    fromInput.dispatchEvent(new Event('change'));
    toInput.value = '2026-01-31';
    toInput.dispatchEvent(new Event('change'));
    await settle(fixture);

    expect(api.callsOf(getCost).at(-1)).toEqual({ by: 'day', from: '2026-01-01', to: '2026-01-31' });
  });

  it('shows the empty state without a chart or a total when there are no rows', async () => {
    const { element } = await render(() => answer({ rows: [] }));

    expect(element.textContent).toContain('No jobs in this range');
    expect(element.querySelector('app-cost-chart')).toBeNull();
    expect(element.querySelectorAll('tr.mat-mdc-row').length).toBe(0);
  });

  it('shows the error state with a retry when the first call fails', async () => {
    const { element } = await render(() => {
      throw new HttpErrorResponse({ status: 400, error: { message: 'from must be before to.' } });
    });

    expect(element.textContent).toContain('Could not load the cost');
    expect(element.textContent).toContain('from must be before to.');
    expect(element.textContent).toContain('Try again');
  });
});
