import { HarnessLoader } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { MatButtonHarness } from '@angular/material/button/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialogHarness } from '@angular/material/dialog/testing';
import { MatSnackBarHarness } from '@angular/material/snack-bar/testing';
import { Api } from '../../api/api';
import { deleteOrphanWorkspace } from '../../api/fn/workspaces/delete-orphan-workspace';
import { fetchWorkspace } from '../../api/fn/workspaces/fetch-workspace';
import { getWorkspaces } from '../../api/fn/workspaces/get-workspaces';
import { recloneWorkspace } from '../../api/fn/workspaces/reclone-workspace';
import { WorkspacesResponse } from '../../api/models/workspaces-response';
import { FakeApi, fixedClock, sampleClone, settle } from '../../shared/testing';
import { BUSY_TOOLTIP, Workspaces } from './workspaces';

/** Two projects: `shop` with two clones (one detached with changes and warnings), `tools` not cloned yet. */
function sampleWorkspaces(overrides: Partial<WorkspacesResponse> = {}): WorkspacesResponse {
  return {
    projects: [
      {
        name: 'shop',
        isBusy: false,
        clones: [
          sampleClone(),
          sampleClone({
            repo: 'team/shop-web',
            folder: 'shop-web',
            components: ['web'],
            branch: null,
            detachedAt: 'origin/main',
            changedPaths: ['src/a.ts', 'src/b.ts'],
            warnings: [
              { code: 'has-changes', message: 'The clone holds 2 changed files between jobs.' },
              {
                code: 'agent-override',
                message: '.github/agents/implement-issue.agent.md overrides the shared agent.',
              },
            ],
          }),
        ],
      },
      {
        name: 'tools',
        isBusy: false,
        clones: [
          sampleClone({
            repo: 'team/tools',
            folder: 'tools',
            exists: false,
            components: ['cli'],
            headSha: null,
            headSubject: null,
            headAt: null,
            branch: null,
            lastFetchAt: null,
            sizeBytes: null,
          }),
        ],
      },
    ],
    orphans: [{ folder: 'old-project', sizeBytes: 1536 }],
    ...overrides,
  };
}

describe('Workspaces', () => {
  let api: FakeApi;

  async function render() {
    await TestBed.configureTestingModule({
      imports: [Workspaces],
      providers: [
        fixedClock,
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: Api, useValue: api },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(Workspaces);
    await settle(fixture);

    return {
      fixture,
      element: fixture.nativeElement as HTMLElement,
      loader: TestbedHarnessEnvironment.loader(fixture),
      root: TestbedHarnessEnvironment.documentRootLoader(fixture),
    };
  }

  function texts(element: HTMLElement, testId: string): string[] {
    return [...element.querySelectorAll(`[data-testid="${testId}"]`)].map(
      (node) => node.textContent?.replace(/\s+/g, ' ').trim() ?? '',
    );
  }

  /** The text of each warning note, without its icon name. */
  function warnings(element: HTMLElement): string[] {
    return [...element.querySelectorAll('[data-testid="clone-warning"] .text')].map(
      (node) => node.textContent?.replace(/\s+/g, ' ').trim() ?? '',
    );
  }

  function cards(element: HTMLElement): HTMLElement[] {
    return [...element.querySelectorAll<HTMLElement>('[data-testid="project-card"]')];
  }

  async function buttons(loader: HarnessLoader, testId: string): Promise<MatButtonHarness[]> {
    return loader.getAllHarnesses(MatButtonHarness.with({ selector: `[data-testid="${testId}"]` }));
  }

  async function choose(root: HarnessLoader, label: string): Promise<void> {
    const dialog = await root.getHarness(MatDialogHarness);
    const choice = await dialog.getHarness(MatButtonHarness.with({ text: label }));
    await choice.click();
  }

  async function snackText(root: HarnessLoader): Promise<string> {
    const snackBar = await root.getHarness(MatSnackBarHarness);

    return snackBar.getMessage();
  }

  beforeEach(() => {
    api = new FakeApi();
  });

  it('shows one card per project with its clones, warnings and the orphan folders', async () => {
    api.answer(getWorkspaces, () => sampleWorkspaces());

    const { element } = await render();
    const [shop, tools] = cards(element);

    expect(cards(element).map((card) => card.querySelector('h2')?.textContent?.trim())).toEqual([
      'shop',
      'tools',
    ]);
    expect(texts(shop, 'clone-repo')).toEqual(['team/shop', 'team/shop-web']);
    expect(texts(shop, 'clone-components')).toEqual(['api, web', 'web']);
    expect(texts(shop, 'clone-checkout')).toEqual(['main', 'detached at origin/main']);
    expect(shop.querySelector('[data-testid="clone-head"] .sha')?.textContent).toBe('0123456');
    expect(shop.querySelector('[data-testid="clone-head"] .subject')?.textContent).toBe(
      'Export orders as CSV',
    );
    expect(texts(shop, 'clone-changes')).toEqual(['0', '2']);
    expect(texts(shop, 'clone-size')).toEqual(['2.0 MB', '2.0 MB']);
    expect(warnings(shop)).toEqual([
      'team/shop-web: The clone holds 2 changed files between jobs.',
      'team/shop-web: .github/agents/implement-issue.agent.md overrides the shared agent.',
    ]);
    expect(shop.querySelector('[data-testid="clone-warning"]')?.classList).toContain(
      'tone-warning',
    );
    expect(warnings(tools)).toEqual([]);
    expect(element.querySelector('[data-testid="project-busy"]')).toBeNull();

    expect(texts(element, 'orphan-folder')).toEqual(['old-project']);
    expect(texts(element, 'orphan-size')).toEqual(['1.5 KB']);
    expect(element.querySelector('[data-testid="orphans-empty"]')).toBeNull();
  });

  it('says "not cloned yet", "never" fetched and "calculating..." for a size not known yet', async () => {
    api.answer(getWorkspaces, () => {
      const data = sampleWorkspaces({ orphans: [{ folder: 'old-project', sizeBytes: null }] });
      data.projects[0].clones[0] = sampleClone({ lastFetchAt: null, sizeBytes: null });

      return data;
    });

    const { element } = await render();
    const [shop, tools] = cards(element);

    expect(texts(shop, 'clone-fetch')[0]).toBe('never');
    expect(texts(shop, 'clone-fetch')[1]).not.toBe('never');
    expect(texts(shop, 'clone-size')).toEqual(['calculating...', '2.0 MB']);
    expect(texts(tools, 'clone-checkout')).toEqual(['Not cloned yet']);
    expect(texts(tools, 'clone-size')).toEqual(['calculating...']);
    expect(texts(element, 'orphan-size')).toEqual(['calculating...']);
  });

  it('marks a busy project and disables its Fetch and Re-clone buttons', async () => {
    api.answer(getWorkspaces, () => {
      const data = sampleWorkspaces();
      data.projects[0] = { ...data.projects[0], isBusy: true };

      return data;
    });

    const { element, loader } = await render();
    const [shop] = cards(element);
    const chip = shop.querySelector('[data-testid="project-busy"]');

    expect(chip?.textContent?.trim()).toContain('Busy');
    expect(chip?.getAttribute('title')).toBe(BUSY_TOOLTIP);

    const fetchButtons = await buttons(loader, 'clone-fetch-button');
    const recloneButtons = await buttons(loader, 'clone-reclone-button');
    const states = await Promise.all(
      [...fetchButtons, ...recloneButtons].map((button) => button.isDisabled()),
    );

    // shop's two clones are disabled, tools' one is not (fetch buttons first, then re-clone buttons).
    expect(states).toEqual([true, true, false, true, true, false]);
  });

  it('fetches a clone with the project name and the folder, shows the message and reloads', async () => {
    api.answer(getWorkspaces, () => sampleWorkspaces());
    api.answer(fetchWorkspace, () => ({ message: 'Fetched team/shop-web.' }));
    const { fixture, loader, root } = await render();
    const pollsBefore = api.callsOf(getWorkspaces).length;

    const [, second] = await buttons(loader, 'clone-fetch-button');
    await second.click();
    await settle(fixture);

    expect(api.callsOf(fetchWorkspace)).toEqual([{ project: 'shop', repo: 'shop-web' }]);
    expect(await snackText(root)).toBe('Fetched team/shop-web.');
    expect(api.callsOf(getWorkspaces).length).toBeGreaterThan(pollsBefore);
  });

  it('shows the runner message of a refused fetch (409) as sent', async () => {
    api.answer(getWorkspaces, () => sampleWorkspaces());
    api.answer(fetchWorkspace, () => {
      throw new HttpErrorResponse({
        status: 409,
        error: { message: 'A job is using this project; try again when it has ended.' },
      });
    });
    const { fixture, loader, root } = await render();

    const [first] = await buttons(loader, 'clone-fetch-button');
    await first.click();
    await settle(fixture);

    expect(await snackText(root)).toBe('A job is using this project; try again when it has ended.');
  });

  it('asks before a re-clone; "Keep clone" makes no call', async () => {
    api.answer(getWorkspaces, () => sampleWorkspaces());
    const { fixture, loader, root } = await render();

    const [first] = await buttons(loader, 'clone-reclone-button');
    await first.click();
    const dialog = await root.getHarness(MatDialogHarness);

    expect(await dialog.getRole()).toBe('alertdialog');
    expect(await dialog.getTitleText()).toBe('Re-clone team/shop?');
    expect(await dialog.getContentText()).toContain('next job');

    await choose(root, 'Keep clone');
    await settle(fixture);

    expect(api.callsOf(recloneWorkspace)).toEqual([]);
  });

  it('re-clones after the confirmation with the project name and the folder', async () => {
    api.answer(getWorkspaces, () => sampleWorkspaces());
    api.answer(recloneWorkspace, () => ({
      message: 'Deleted the clone; the next job clones it again.',
    }));
    const { fixture, loader, root } = await render();

    const [, second] = await buttons(loader, 'clone-reclone-button');
    await second.click();
    await choose(root, 'Re-clone');
    await settle(fixture);

    expect(api.callsOf(recloneWorkspace)).toEqual([{ project: 'shop', repo: 'shop-web' }]);
    expect(await snackText(root)).toBe('Deleted the clone; the next job clones it again.');
  });

  it('asks before it deletes an orphan folder; "Keep folder" makes no call', async () => {
    api.answer(getWorkspaces, () => sampleWorkspaces());
    const { fixture, loader, root } = await render();

    const [deleteButton] = await buttons(loader, 'orphan-delete-button');
    await deleteButton.click();
    const dialog = await root.getHarness(MatDialogHarness);

    expect(await dialog.getTitleText()).toBe('Delete the folder old-project?');

    await choose(root, 'Keep folder');
    await settle(fixture);

    expect(api.callsOf(deleteOrphanWorkspace)).toEqual([]);
  });

  it('deletes an orphan folder after the confirmation and shows the message', async () => {
    api.answer(getWorkspaces, () => sampleWorkspaces());
    api.answer(deleteOrphanWorkspace, () => ({ message: 'Deleted work/old-project.' }));
    const { fixture, loader, root } = await render();

    const [deleteButton] = await buttons(loader, 'orphan-delete-button');
    await deleteButton.click();
    await choose(root, 'Delete folder');
    await settle(fixture);

    expect(api.callsOf(deleteOrphanWorkspace)).toEqual([{ folder: 'old-project' }]);
    expect(await snackText(root)).toBe('Deleted work/old-project.');
  });

  it('shows an empty state without projects and a short note without orphan folders', async () => {
    api.answer(getWorkspaces, () => ({ projects: [], orphans: [] }));

    const { element } = await render();

    expect(cards(element)).toEqual([]);
    expect(element.textContent).toContain('No projects');
    expect(texts(element, 'orphans-empty')).toEqual(['No orphan folders.']);
    expect(element.querySelector('[data-testid="orphan-folder"]')).toBeNull();
  });

  it('shows an error with a retry when the workspaces cannot be read', async () => {
    api.answer(getWorkspaces, () => {
      throw new HttpErrorResponse({ status: 0 });
    });

    const { element } = await render();

    expect(element.textContent).toContain('Could not load the workspaces');
    expect(element.textContent).toContain('The runner is not reachable.');
    expect(cards(element)).toEqual([]);
  });
});
