import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { IssueRowResponse } from '../../api/models/issue-row-response';
import { Api } from '../../api/api';
import { runIssueAction } from '../../api/fn/issues/run-issue-action';
import { FakeApi, fixedClock, sampleIssue, samplePull, settle } from '../testing';
import { IssueCard } from './issue-card';

describe('IssueCard', () => {
  let api: FakeApi;
  let fixture: ComponentFixture<IssueCard>;

  beforeEach(() => {
    api = new FakeApi();
  });

  async function render(issue: IssueRowResponse) {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), fixedClock, { provide: Api, useValue: api }],
    });

    fixture = TestBed.createComponent(IssueCard);
    fixture.componentRef.setInput('issue', issue);
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  it('shows the number, where it lives, the title linked to the detail page and the GitBucket link', async () => {
    const element = await render(sampleIssue());

    expect(element.querySelector('.number')?.textContent).toBe('#42');
    expect(element.querySelector('.where')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('shop · api');
    expect(element.querySelector('a.title')?.getAttribute('href')).toBe('/issues/team/shop/42');
    expect((element.querySelector('a.external') as HTMLAnchorElement).href).toBe(
      'http://gitbucket.local/team/shop/issues/42',
    );
  });

  it('shows the stage, the badge and the flags as chips', async () => {
    const element = await render(
      sampleIssue({
        badge: { code: 'queued', label: 'Queued' },
        flags: [{ code: 'failed', label: 'Failed' }],
      }),
    );

    const chips = Array.from(element.querySelectorAll('.chips app-status-chip .label'));
    expect(chips.map((chip) => chip.textContent)).toEqual(['Ready - simple', 'Queued', 'Failed']);
  });

  it('shows what a person must do, the PRs and nothing about a parent for a top-level issue', async () => {
    const element = await render(
      sampleIssue({
        waiting: { code: 'merge-pr', label: 'Review and merge PR #50' },
        pulls: [samplePull(), samplePull({ number: 51, state: 'merged', stateLabel: 'Merged' })],
      }),
    );

    expect(element.querySelector('.waiting')?.textContent?.trim()).toContain('Review and merge PR #50');
    expect(Array.from(element.querySelectorAll('app-pull-link')).map((pull) => pull.textContent?.trim())).toEqual([
      '#50',
      '#51',
    ]);
    expect(element.querySelector('.parent')).toBeNull();
    expect(element.querySelector('app-sub-issue-progress')).toBeNull();
  });

  it('links a sub-issue to its parent and shows a parent\'s progress', async () => {
    const subIssue = await render(sampleIssue({ number: 20, parent: 12 }));
    expect(subIssue.querySelector('.parent')?.textContent?.trim()).toBe('Part of #12');
    expect(subIssue.querySelector('.parent a')?.getAttribute('href')).toBe('/issues/team/shop/12');

    TestBed.resetTestingModule();
    const parent = await render(sampleIssue({ number: 12, progress: { closed: 1, total: 3 } }));
    expect(parent.querySelector('app-sub-issue-progress .text')?.textContent).toBe('1/3');
  });

  it('shows the actions of the issue and tells the screen to reload after one ran', async () => {
    api.answer(runIssueAction, () => ({ message: 'Added ai-implement to #42.' }));
    const element = await render(
      sampleIssue({
        actions: [
          { code: 'implement', label: 'Implement', needsText: false },
          { code: 'ask', label: 'Ask a question', needsText: true },
        ],
      }),
    );
    let afters = 0;
    fixture.componentInstance.after.subscribe(() => afters++);

    const buttons = Array.from(element.querySelectorAll<HTMLButtonElement>('app-action-buttons button'));
    expect(buttons.map((item) => item.textContent?.trim())).toEqual(['Implement', 'Ask a question']);

    buttons[0].click();
    await settle(fixture);

    expect(api.callsOf(runIssueAction)).toEqual([
      { owner: 'team', repo: 'shop', number: 42, code: 'implement', body: { text: null } },
    ]);
    expect(afters).toBe(1);
  });
});
