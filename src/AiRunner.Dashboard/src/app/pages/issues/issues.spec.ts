import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { MatSelectHarness } from '@angular/material/select/testing';
import { MatSlideToggleHarness } from '@angular/material/slide-toggle/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Api } from '../../api/api';
import { GetIssues$Params, getIssues } from '../../api/fn/issues/get-issues';
import { IssueListResponse } from '../../api/models/issue-list-response';
import { FakeApi, fixedClock, sampleIssue, samplePull, settle } from '../../shared/testing';
import { Issues, SEARCH_DEBOUNCE_MS } from './issues';

describe('Issues', () => {
  function list(overrides: Partial<IssueListResponse> = {}): IssueListResponse {
    return {
      items: [
        sampleIssue({
          number: 12,
          title: 'Checkout redesign',
          stage: 'broken-down',
          stageLabel: 'Broken down (1/2)',
          lane: 'in-progress',
          laneLabel: 'In progress',
          progress: { closed: 1, total: 2 },
          waiting: { code: 'start-next', label: 'Start the next sub-issue' },
          subIssues: [
            sampleIssue({
              number: 20,
              parent: 12,
              stage: 'done',
              stageLabel: 'Done',
              lane: 'finished',
              laneLabel: 'Done',
              state: 'closed',
              stateLabel: 'Closed',
              pulls: [samplePull({ number: 50, state: 'merged', stateLabel: 'Merged' })],
            }),
            sampleIssue({
              number: 21,
              parent: 12,
              stage: 'implementing',
              stageLabel: 'Implementing',
              lane: 'in-progress',
              laneLabel: 'In progress',
              badge: { code: 'running', label: 'Running' },
              waiting: { code: 'wait', label: 'Should not show under a badge' },
              flags: [{ code: 'claimed', label: 'Claimed by laptop-1' }],
            }),
          ],
        }),
        sampleIssue({
          number: 7,
          htmlUrl: null,
          component: null,
          flags: [{ code: 'failed', label: 'Failed' }],
          pulls: [samplePull({ number: 51 })],
        }),
      ],
      projects: [
        { name: 'shop', components: ['api', 'web'] },
        { name: 'blog', components: ['blog'] },
      ],
      stages: [
        { code: 'implementing', label: 'Implementing' },
        { code: 'pr-open', label: 'PR open' },
      ],
      total: 16,
      lanes: [
        { code: 'todo', label: 'To do', count: 4 },
        { code: 'ready', label: 'Ready', count: 1 },
        { code: 'in-progress', label: 'In progress', count: 2 },
        { code: 'in-review', label: 'In review', count: 0 },
        { code: 'finished', label: 'Done', count: 9 },
      ],
      focuses: [
        { code: 'needs-you', label: 'Needs you', count: 3 },
        { code: 'working', label: 'Runner working', count: 1 },
        { code: 'failed', label: 'Failed', count: 1 },
      ],
      ...overrides,
    };
  }

  /** The flat answer the board asks for: every issue and sub-issue on its own. */
  function flatList(): IssueListResponse {
    const nested = list();
    const items = nested.items.flatMap((item) => [{ ...item, subIssues: [] }, ...item.subIssues]);

    return { ...nested, items };
  }

  async function render(url = '/issues', reply: (params: GetIssues$Params) => IssueListResponse = () => list()) {
    const api = new FakeApi().answer(getIssues, reply);

    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'issues', component: Issues }]),
        fixedClock,
        { provide: Api, useValue: api },
      ],
    });

    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, Issues);
    await settle(harness.fixture);

    const loader = TestbedHarnessEnvironment.loader(harness.fixture);
    const router = TestBed.inject(Router);
    const element = harness.routeNativeElement as HTMLElement;

    return { api, harness, loader, router, element, fixture: harness.fixture };
  }

  function rowsOf(element: HTMLElement): HTMLElement[] {
    return Array.from(element.querySelectorAll<HTMLElement>('tr.mat-mdc-row'));
  }

  function numbersOf(element: HTMLElement): (string | undefined)[] {
    return rowsOf(element).map((row) => row.querySelector('.number')?.textContent ?? undefined);
  }

  function lastCall(api: FakeApi): GetIssues$Params {
    return api.callsOf(getIssues).at(-1) as GetIssues$Params;
  }

  function click(element: HTMLElement | null): void {
    (element as HTMLElement).click();
  }

  describe('filters in the URL', () => {
    it('asks for open issues by default', async () => {
      const { api } = await render();

      expect(lastCall(api)).toEqual({
        project: undefined,
        component: undefined,
        stage: undefined,
        state: 'open',
        flat: undefined,
        lane: undefined,
        focus: undefined,
        q: undefined,
        sort: undefined,
      });
    });

    it('sends state=all and every other filter from the URL', async () => {
      const { api } = await render(
        '/issues?state=all&project=shop&component=web&stage=pr-open&lane=ready&focus=needs-you&q=login&sort=updated&flat=true',
      );

      expect(lastCall(api)).toEqual({
        project: 'shop',
        component: 'web',
        stage: 'pr-open',
        state: 'all',
        flat: true,
        lane: 'ready',
        focus: 'needs-you',
        q: 'login',
        sort: 'updated',
      });
    });

    it('writes a chosen filter into the URL and asks the API again at once', async () => {
      const { api, fixture, loader, router } = await render();

      const [, , , stageSelect] = await loader.getAllHarnesses(MatSelectHarness);
      await stageSelect.open();
      await stageSelect.clickOptions({ text: 'PR open' });
      await settle(fixture);

      expect(router.url).toBe('/issues?stage=pr-open');
      expect(lastCall(api)).toMatchObject({ stage: 'pr-open', state: 'open' });
    });

    it('writes state=all for All and removes state for Open, the default', async () => {
      const { api, fixture, loader, router } = await render();
      const [, , stateSelect] = await loader.getAllHarnesses(MatSelectHarness);

      await stateSelect.open();
      await stateSelect.clickOptions({ text: 'All' });
      await settle(fixture);
      expect(router.url).toBe('/issues?state=all');
      expect(lastCall(api).state).toBe('all');

      await stateSelect.open();
      await stateSelect.clickOptions({ text: 'Open' });
      await settle(fixture);
      expect(router.url).toBe('/issues');
      expect(lastCall(api).state).toBe('open');
    });

    it('offers the components of the chosen project only and clears the component when the project changes', async () => {
      const { api, fixture, loader } = await render('/issues?component=web');
      const [projectSelect, componentSelect] = await loader.getAllHarnesses(MatSelectHarness);

      await projectSelect.open();
      await projectSelect.clickOptions({ text: 'blog' });
      await settle(fixture);

      expect(lastCall(api)).toMatchObject({ project: 'blog', component: undefined });

      await componentSelect.open();
      const options = await componentSelect.getOptions();
      const texts = await Promise.all(options.map((option) => option.getText()));
      expect(texts).toEqual(['All components', 'blog']);
    });

    it('asks for the flat list when the toggle is on', async () => {
      const { api, fixture, loader } = await render();

      const toggle = await loader.getHarness(MatSlideToggleHarness);
      await toggle.toggle();
      await settle(fixture);

      expect(lastCall(api)).toMatchObject({ flat: true });
    });

    it('sends the search after a pause in typing', async () => {
      const { api, fixture, element, router } = await render();
      const input = element.querySelector('.search input') as HTMLInputElement;

      input.value = '#12';
      input.dispatchEvent(new Event('input'));
      await new Promise((resolve) => setTimeout(resolve, SEARCH_DEBOUNCE_MS + 50));
      await settle(fixture);

      expect(router.url).toBe('/issues?q=%2312');
      expect(lastCall(api)).toMatchObject({ q: '#12' });
    });

    it('offers Clear filters only when a filter differs from the defaults, and clearing keeps the view', async () => {
      const plain = await render();
      expect(plain.element.querySelector('button.clear')).toBeNull();

      TestBed.resetTestingModule();
      const { element, fixture, router } = await render('/issues?view=board&state=closed&q=login');
      click(element.querySelector('button.clear'));
      await settle(fixture);

      expect(router.url).toBe('/issues?view=board');
    });
  });

  describe('focus chips and pipeline', () => {
    it('shows the counts the server sent and sets the focus filter', async () => {
      const { api, element, fixture, router } = await render();

      const chips = Array.from(element.querySelectorAll<HTMLButtonElement>('.focuses button'));
      expect(chips.map((chip) => chip.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
        'All 16',
        'Needs you 3',
        'Runner working 1',
        'Failed 1',
      ]);
      expect(chips[0].getAttribute('aria-pressed')).toBe('true');
      expect(chips[1].classList).toContain('tone-warning');

      click(chips[1]);
      await settle(fixture);

      expect(router.url).toBe('/issues?focus=needs-you');
      expect(lastCall(api)).toMatchObject({ focus: 'needs-you' });
      expect(element.querySelector('[data-focus="needs-you"]')?.getAttribute('aria-pressed')).toBe('true');
    });

    it('shows one segment per lane with the server count and toggles the lane filter', async () => {
      const { api, element, fixture, router } = await render();

      const segments = Array.from(element.querySelectorAll<HTMLButtonElement>('.pipeline button'));
      expect(segments.map((segment) => segment.querySelector('.segment-count')?.textContent)).toEqual([
        '4',
        '1',
        '2',
        '0',
        '9',
      ]);
      expect(segments.map((segment) => segment.querySelector('.segment-label')?.textContent?.trim())).toEqual([
        'To do',
        'Ready',
        'In progress',
        'In review',
        'Done',
      ]);
      expect(segments[4].classList).toContain('tone-success');

      click(segments[1]);
      await settle(fixture);
      expect(router.url).toBe('/issues?lane=ready');
      expect(lastCall(api)).toMatchObject({ lane: 'ready' });

      const ready = element.querySelector<HTMLButtonElement>('.pipeline [data-lane="ready"]');
      expect(ready?.getAttribute('aria-pressed')).toBe('true');

      click(ready);
      await settle(fixture);
      expect(router.url).toBe('/issues');
      expect(lastCall(api).lane).toBeUndefined();
    });
  });

  describe('list view', () => {
    it('collapses sub-issues by default and expands one parent with its chevron', async () => {
      const { element, fixture } = await render();

      expect(numbersOf(element)).toEqual(['#12', '#7']);

      const chevron = rowsOf(element)[0].querySelector('button.chevron') as HTMLButtonElement;
      expect(chevron.getAttribute('aria-expanded')).toBe('false');
      expect(chevron.getAttribute('aria-label')).toBe('Show sub-issues of #12');
      expect(rowsOf(element)[1].querySelector('button.chevron')).toBeNull();

      click(chevron);
      await settle(fixture);

      expect(numbersOf(element)).toEqual(['#12', '#20', '#21', '#7']);
      expect(rowsOf(element)[1].querySelector('.issue')?.classList).toContain('sub-issue');
      expect(rowsOf(element)[0].querySelector('button.chevron')?.getAttribute('aria-expanded')).toBe('true');
    });

    it('expands and collapses every parent with the text button', async () => {
      const { element, fixture } = await render();
      const button = element.querySelector('button.expand-all') as HTMLButtonElement;

      expect(button.textContent?.trim()).toBe('Expand all');
      click(button);
      await settle(fixture);
      expect(numbersOf(element)).toEqual(['#12', '#20', '#21', '#7']);
      expect(button.textContent?.trim()).toBe('Collapse all');

      click(button);
      await settle(fixture);
      expect(numbersOf(element)).toEqual(['#12', '#7']);
    });

    it('expands the parents when a lane, focus, stage or search narrows the list', async () => {
      const { element } = await render('/issues?focus=working');

      expect(numbersOf(element)).toEqual(['#12', '#20', '#21', '#7']);
    });

    it('shows the badge in Next over the action, the action when there is no badge, and "-" otherwise', async () => {
      const { element } = await render('/issues?q=x');
      const nextOf = (index: number) => rowsOf(element)[index].querySelector('.next') as HTMLElement;

      const action = nextOf(0).querySelector('app-status-chip') as HTMLElement;
      expect(action.querySelector('.label')?.textContent).toBe('Start the next sub-issue');
      expect(action.classList).toContain('tone-warning');

      const badge = nextOf(2).querySelector('app-status-chip') as HTMLElement;
      expect(badge.querySelector('.label')?.textContent).toBe('Running');
      expect(badge.classList).toContain('tone-progress');
      expect(nextOf(2).textContent).not.toContain('Should not show');

      expect(nextOf(3).textContent?.trim()).toBe('-');
    });

    it('shows the stage, the parent progress, flags and PR links with their tone', async () => {
      const { element } = await render('/issues?q=x');
      const rows = rowsOf(element);

      const stage = rows[0].querySelector('app-stage-chip app-status-chip') as HTMLElement;
      expect(stage.textContent?.trim()).toBe('Broken down (1/2)');
      expect(stage.classList).toContain('tone-progress');
      expect(rows[0].querySelector('app-sub-issue-progress .text')?.textContent).toBe('1/2');
      expect(rows[3].querySelector('app-sub-issue-progress')).toBeNull();

      const flag = rows[3].querySelector('.marks app-status-chip') as HTMLElement;
      expect(flag.querySelector('.label')?.textContent).toBe('Failed');
      expect(flag.classList).toContain('tone-danger');

      const merged = rows[1].querySelector('app-pull-link') as HTMLElement;
      expect(merged.textContent?.trim()).toBe('#50');
      expect(merged.classList).toContain('tone-success');
      expect(rows[3].querySelector('app-pull-link')?.classList).toContain('tone-info');
    });

    it('shows project and component under the title, links to the detail page and to GitBucket', async () => {
      const { element } = await render();
      const rows = rowsOf(element);

      expect(rows[0].querySelector('.where')?.textContent?.replace(/\s+/g, ' ').trim()).toBe('shop · api');
      expect(rows[1].querySelector('.where')?.textContent?.trim()).toBe('shop');
      expect(rows[0].querySelector('a.title')?.getAttribute('href')).toBe('/issues/team/shop/12');

      const external = rows[0].querySelector('a.external') as HTMLAnchorElement;
      expect(external.href).toBe('http://gitbucket.local/team/shop/issues/42');
      expect(external.target).toBe('_blank');
      expect(rows[1].querySelector('a.external')).toBeNull();
    });

    it('says when no issue matches', async () => {
      const { element } = await render('/issues', () => list({ items: [] }));

      expect(element.textContent).toContain('No issues found');
    });
  });

  describe('board view', () => {
    it('asks for the flat list without stage or lane and shows one column per lane', async () => {
      const { api, element } = await render('/issues?view=board&stage=pr-open&lane=ready', () => flatList());

      expect(lastCall(api)).toMatchObject({ flat: true, stage: undefined, lane: undefined, state: 'open' });

      const lanes = Array.from(element.querySelectorAll<HTMLElement>('.board .lane'));
      expect(lanes.map((lane) => lane.querySelector('.lane-label')?.textContent)).toEqual([
        'To do',
        'Ready',
        'In progress',
        'In review',
        'Done',
      ]);
      expect(lanes.map((lane) => lane.querySelector('.count')?.textContent)).toEqual(['4', '1', '2', '0', '9']);

      const numbersIn = (lane: HTMLElement) =>
        Array.from(lane.querySelectorAll('app-issue-card .number')).map((number) => number.textContent);
      expect(numbersIn(lanes[1])).toEqual(['#7']);
      expect(numbersIn(lanes[2])).toEqual(['#12', '#21']);
      expect(numbersIn(lanes[4])).toEqual(['#20']);
      expect(lanes[0].textContent).toContain('Nothing here');
    });

    it('hides the stage select, the flat toggle and the pipeline but keeps the focus chips', async () => {
      const { element, loader } = await render('/issues?view=board', () => flatList());

      expect(await loader.getAllHarnesses(MatSelectHarness)).toHaveLength(4);
      expect(await loader.getAllHarnesses(MatSlideToggleHarness)).toHaveLength(0);
      expect(element.querySelector('.pipeline')).toBeNull();
      expect(element.querySelectorAll('.focuses button')).toHaveLength(4);
    });

    it('switches between list and board through the URL', async () => {
      const { api, element, fixture, router } = await render();

      click(element.querySelector('mat-button-toggle[value="board"] button'));
      await settle(fixture);

      expect(router.url).toBe('/issues?view=board');
      expect(lastCall(api)).toMatchObject({ flat: true });
      expect(element.querySelector('.board')).not.toBeNull();
    });

    it('shows the error state with Try again when the first call fails', async () => {
      const { element } = await render('/issues?view=board', () => {
        throw new Error('The runner is not reachable.');
      });

      expect(element.textContent).toContain('The runner is not reachable.');
      expect(element.textContent).toContain('Try again');
    });
  });
});
