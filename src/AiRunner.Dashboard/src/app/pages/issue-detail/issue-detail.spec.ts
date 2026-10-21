import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Api } from '../../api/api';
import { getIssue } from '../../api/fn/issues/get-issue';
import { runIssueAction } from '../../api/fn/issues/run-issue-action';
import { runPullAction } from '../../api/fn/pulls/run-pull-action';
import { IssueDetailResponse } from '../../api/models/issue-detail-response';
import { FakeApi, fixedClock, sampleIssue, sampleJob, samplePull, settle } from '../../shared/testing';
import { IssueDetail } from './issue-detail';

const SIMPLE: IssueDetailResponse = {
  issue: sampleIssue({
    flags: [{ code: 'failed', label: 'Failed' }],
    waiting: { code: 'add-ai-implement', label: 'Add the ai-implement label' },
    pulls: [samplePull({ title: 'Export orders' })],
  }),
  analysis: {
    status: { code: 'ready', label: 'Ready' },
    complexity: { code: 'simple', label: 'Simple' },
    component: 'api',
  },
  subIssues: [],
  timeline: [sampleJob({ id: 7 }), sampleJob({ id: 6, hasLog: false, result: null, detail: null, copilotCalls: null })],
};

const PARENT: IssueDetailResponse = {
  ...SIMPLE,
  issue: sampleIssue({ number: 12, title: 'Checkout redesign', stage: 'broken-down', stageLabel: 'Broken down (1/2)' }),
  subIssues: [
    sampleIssue({
      number: 20,
      title: 'Cart API',
      stage: 'done',
      stageLabel: 'Done',
      parent: 12,
      pulls: [samplePull({ number: 51, title: 'Cart API', state: 'merged', stateLabel: 'Merged' })],
    }),
  ],
  timeline: [],
};

describe('IssueDetail', () => {
  async function render(reply: () => IssueDetailResponse) {
    const api = new FakeApi().answer(getIssue, reply);

    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'issues/:owner/:repo/:number', component: IssueDetail }]),
        fixedClock,
        { provide: Api, useValue: api },
      ],
    });

    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/issues/team/shop/42', IssueDetail);
    await settle(harness.fixture);

    return { api, element: harness.routeNativeElement as HTMLElement, fixture: harness.fixture };
  }

  it('asks the API for the issue of the route', async () => {
    const { api } = await render(() => SIMPLE);

    expect(api.callsOf(getIssue).at(0)).toEqual({ owner: 'team', repo: 'shop', number: 42 });
  });

  it('shows the header, stage, flags, waiting action and analysis of a simple issue', async () => {
    const { element } = await render(() => SIMPLE);

    expect(element.querySelector('h1')?.textContent).toContain('Export orders as CSV');
    expect(element.querySelector('app-stage-chip')?.textContent).toContain('Ready - simple');
    expect(element.querySelector('app-status-chip.tone-danger')?.textContent).toContain('Failed');
    expect(element.querySelector('a[href="http://gitbucket.local/team/shop/issues/42"]')).not.toBeNull();

    const facts = element.querySelector('.facts') as HTMLElement;
    expect(facts.textContent).toContain('Add the ai-implement label');
    expect(facts.textContent).toContain('Simple');
    expect(facts.textContent).toContain('api');
    expect(element.querySelector('#sub-issues-title')).toBeNull();
  });

  it('lists the pull request and the job timeline with a log link only when a log exists', async () => {
    const { element } = await render(() => SIMPLE);

    const pulls = element.querySelector('[aria-labelledby="pulls-title"]') as HTMLElement;
    expect(pulls.textContent).toContain('Export orders');
    expect(pulls.textContent).toContain('ai/issue-42 -> main');
    expect(pulls.textContent).toContain('Open');

    const timeline = Array.from(
      element.querySelectorAll('[aria-labelledby="timeline-title"] tr.mat-mdc-row'),
    );
    expect(timeline.length).toBe(2);
    expect(timeline[0].textContent).toContain('PR opened');
    expect(timeline[0].textContent).toContain('3 min 05 s');
    expect(timeline[0].querySelector('a')?.getAttribute('href')).toBe('/jobs/7');
    expect(timeline[1].querySelector('a')).toBeNull();
    expect(timeline[0].querySelector('.mat-column-copilotCalls')?.textContent?.trim()).toBe('2');
    expect(timeline[1].querySelector('.mat-column-copilotCalls')?.textContent?.trim()).toBe('-');
  });

  it('shows sub-issues with a link to their detail page, stage and PR', async () => {
    const { element } = await render(() => PARENT);

    expect(element.querySelector('.facts')?.textContent).not.toContain('Waiting on you');

    const row = element.querySelector('[aria-labelledby="sub-issues-title"] tr.mat-mdc-row') as HTMLElement;
    expect(row.querySelector('a')?.getAttribute('href')).toBe('/issues/team/shop/20');
    expect(row.querySelector('app-stage-chip app-status-chip')?.classList).toContain('tone-success');
    const pull = row.querySelector('app-pull-link') as HTMLElement;
    expect(pull.textContent?.trim()).toBe('#51');
    expect(pull.getAttribute('data-state')).toBe('merged');
  });

  it('shows the not-found state when the API answers 404', async () => {
    const { element } = await render(() => {
      throw new HttpErrorResponse({ status: 404, statusText: 'Not Found' });
    });

    expect(element.textContent).toContain('Issue not found');
  });

  it('shows an error state with a retry for another failure', async () => {
    const { element } = await render(() => {
      throw new HttpErrorResponse({ status: 500, statusText: 'Server Error' });
    });

    expect(element.textContent).toContain('Could not load this issue');
    expect(element.textContent).toContain('Try again');
  });

  it('shows the actions of the issue in the header and of each PR in its row, and reloads after one ran', async () => {
    const withActions: IssueDetailResponse = {
      ...SIMPLE,
      issue: {
        ...SIMPLE.issue,
        actions: [
          { code: 'retry', label: 'Retry', needsText: false },
          { code: 'implement', label: 'Implement', needsText: false },
        ],
        pulls: [samplePull({ actions: [{ code: 'review-full', label: 'Full review', needsText: false }] })],
      },
    };
    const { api, element, fixture } = await render(() => withActions);
    api.answer(runIssueAction, () => ({ message: 'Removed ai:failed from #42.' }));
    api.answer(runPullAction, () => ({ message: 'A full review is asked for.' }));

    const header = element.querySelector('[data-testid="issue-actions"]') as HTMLElement;
    const headerButtons = Array.from(header.querySelectorAll<HTMLButtonElement>('button'));
    expect(headerButtons.map((button) => button.textContent?.trim())).toEqual(['Retry', 'Implement']);

    const pullButton = element.querySelector<HTMLButtonElement>(
      '[aria-labelledby="pulls-title"] app-action-buttons button',
    ) as HTMLButtonElement;
    expect(pullButton.textContent?.trim()).toBe('Full review');

    const detailCallsBefore = api.callsOf(getIssue).length;
    headerButtons[0].click();
    await settle(fixture);
    pullButton.click();
    await settle(fixture);

    expect(api.callsOf(runIssueAction)).toEqual([
      { owner: 'team', repo: 'shop', number: 42, code: 'retry', body: { text: null } },
    ]);
    expect(api.callsOf(runPullAction)).toEqual([
      { owner: 'team', repo: 'shop', number: 50, code: 'review-full', body: { text: null } },
    ]);
    expect(api.callsOf(getIssue).length).toBeGreaterThan(detailCallsBefore);
  });

  it('shows the waiting text and the actions of each sub-issue in its row', async () => {
    const withActions: IssueDetailResponse = {
      ...PARENT,
      subIssues: [
        {
          ...PARENT.subIssues[0],
          waiting: { code: 'start-next-sub-issue', label: 'Start the next sub-issue' },
          actions: [{ code: 'start-next-sub-issue', label: 'Start next sub-issue', needsText: false }],
        },
      ],
    };
    const { element } = await render(() => withActions);

    const row = element.querySelector('[aria-labelledby="sub-issues-title"] tr.mat-mdc-row') as HTMLElement;
    const next = row.querySelector('.next') as HTMLElement;
    expect(next.textContent).toContain('Start the next sub-issue');
    expect(next.querySelector('app-action-buttons button')?.textContent?.trim()).toBe('Start next sub-issue');
  });
});
