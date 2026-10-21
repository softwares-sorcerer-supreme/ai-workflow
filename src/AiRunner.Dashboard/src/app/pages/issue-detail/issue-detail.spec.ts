import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Api } from '../../api/api';
import { getIssue } from '../../api/fn/issues/get-issue';
import { IssueDetailResponse } from '../../api/models/issue-detail-response';
import { FakeApi, fixedClock, sampleIssue, sampleJob, settle } from '../../shared/testing';
import { IssueDetail } from './issue-detail';

const SIMPLE: IssueDetailResponse = {
  issue: sampleIssue({
    flags: [{ code: 'failed', label: 'Failed' }],
    waiting: { code: 'add-ai-implement', label: 'Add the ai-implement label' },
    pulls: [
      {
        repo: 'team/shop',
        number: 50,
        title: 'Export orders',
        state: 'open',
        stateLabel: 'Open',
        headRef: 'ai/issue-42',
        baseRef: 'main',
        htmlUrl: 'http://gitbucket.local/team/shop/pull/50',
        updatedAt: '2026-01-01T11:00:00Z',
      },
    ],
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
      pulls: [
        {
          repo: 'team/shop',
          number: 51,
          title: 'Cart API',
          state: 'merged',
          stateLabel: 'Merged',
          headRef: 'ai/issue-12-20',
          baseRef: 'feat/issue-12',
          htmlUrl: 'http://gitbucket.local/team/shop/pull/51',
          updatedAt: '2026-01-01T11:00:00Z',
        },
      ],
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

    return { api, element: harness.routeNativeElement as HTMLElement };
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
});
