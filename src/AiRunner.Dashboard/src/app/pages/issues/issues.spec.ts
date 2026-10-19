import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { MatSelectHarness } from '@angular/material/select/testing';
import { MatSlideToggleHarness } from '@angular/material/slide-toggle/testing';
import { Api } from '../../api/api';
import { getIssues } from '../../api/fn/issues/get-issues';
import { IssueListResponse } from '../../api/models/issue-list-response';
import { FakeApi, fixedClock, sampleIssue, settle } from '../../shared/testing';
import { Issues } from './issues';

describe('Issues', () => {
  function list(overrides: Partial<IssueListResponse> = {}): IssueListResponse {
    return {
      items: [
        sampleIssue({
          number: 12,
          title: 'Checkout redesign',
          stage: 'broken-down',
          stageLabel: 'Broken down (1/2)',
          subIssues: [
            sampleIssue({ number: 20, parent: 12, stage: 'done', stageLabel: 'Done', state: 'closed', stateLabel: 'Closed' }),
            sampleIssue({
              number: 21,
              parent: 12,
              stage: 'implementing',
              stageLabel: 'Implementing',
              badge: { code: 'running', label: 'Running' },
              flags: [{ code: 'claimed', label: 'Claimed by laptop-1' }],
            }),
          ],
        }),
        sampleIssue({ number: 7, htmlUrl: null, flags: [{ code: 'failed', label: 'Failed' }] }),
      ],
      projects: [
        { name: 'shop', components: ['api', 'web'] },
        { name: 'blog', components: ['blog'] },
      ],
      stages: [
        { code: 'implementing', label: 'Implementing' },
        { code: 'pr-open', label: 'PR open' },
      ],
      ...overrides,
    };
  }

  async function render(reply: IssueListResponse = list()) {
    const api = new FakeApi().answer(getIssues, () => reply);

    TestBed.configureTestingModule({
      imports: [Issues],
      providers: [fixedClock, { provide: Api, useValue: api }],
    });

    const fixture = TestBed.createComponent(Issues);
    await settle(fixture);

    const loader = TestbedHarnessEnvironment.loader(fixture);

    return { api, fixture, loader, element: fixture.nativeElement as HTMLElement };
  }

  function rowsOf(element: HTMLElement): HTMLElement[] {
    return Array.from(element.querySelectorAll<HTMLElement>('tr.mat-mdc-row'));
  }

  it('shows each parent followed by its sub-issues, indented', async () => {
    const { element } = await render();

    const rows = rowsOf(element);
    expect(rows.map((row) => row.querySelector('.number')?.textContent)).toEqual(['#12', '#20', '#21', '#7']);
    expect(rows.map((row) => row.querySelector('.issue')?.classList.contains('sub-issue'))).toEqual([
      false,
      true,
      true,
      false,
    ]);
  });

  it('shows the stage label the API sent, colored by its code', async () => {
    const { element } = await render();

    const stage = rowsOf(element)[0].querySelector('app-stage-chip app-status-chip') as HTMLElement;
    expect(stage.textContent?.trim()).toBe('Broken down (1/2)');
    expect(stage.classList).toContain('tone-progress');
  });

  it('shows the badge and the flags as chips with their tone', async () => {
    const { element } = await render();

    const running = Array.from(rowsOf(element)[2].querySelectorAll('.marks app-status-chip'));
    expect(running.map((chip) => chip.querySelector('.label')?.textContent)).toEqual([
      'Running',
      'Claimed by laptop-1',
    ]);
    expect(running.map((chip) => chip.querySelector('mat-icon')?.textContent)).toEqual(['play_circle', 'lock']);
    expect(running[0].classList).toContain('tone-progress');

    const failed = rowsOf(element)[3].querySelector('.marks app-status-chip') as HTMLElement;
    expect(failed.querySelector('.label')?.textContent).toBe('Failed');
    expect(failed.classList).toContain('tone-danger');
  });

  it('links the title to GitBucket in a new tab when the API sends a link', async () => {
    const { element } = await render();

    const link = rowsOf(element)[0].querySelector('a.title') as HTMLAnchorElement;
    expect(link.href).toBe('http://gitbucket.local/team/shop/issues/42');
    expect(link.target).toBe('_blank');
    expect(rowsOf(element)[3].querySelector('a.title')).toBeNull();
  });

  it('asks the API again with the chosen stage', async () => {
    const { api, fixture, loader } = await render();

    const [, , stageSelect] = await loader.getAllHarnesses(MatSelectHarness);
    await stageSelect.open();
    await stageSelect.clickOptions({ text: 'PR open' });
    await settle(fixture);

    expect(api.callsOf(getIssues).at(-1)).toEqual({
      project: undefined,
      component: undefined,
      stage: 'pr-open',
      state: undefined,
      flat: undefined,
    });
  });

  it('offers the components of the chosen project only and clears the component when the project changes', async () => {
    const { api, fixture, loader } = await render();
    const [projectSelect, componentSelect] = await loader.getAllHarnesses(MatSelectHarness);

    await componentSelect.open();
    await componentSelect.clickOptions({ text: 'web' });
    await projectSelect.open();
    await projectSelect.clickOptions({ text: 'blog' });
    await settle(fixture);

    expect(api.callsOf(getIssues).at(-1)).toMatchObject({ project: 'blog', component: undefined });

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

    expect(api.callsOf(getIssues).at(-1)).toMatchObject({ flat: true });
  });

  it('says when no issue matches', async () => {
    const { element } = await render(list({ items: [] }));

    expect(element.textContent).toContain('No issues found');
  });
});
