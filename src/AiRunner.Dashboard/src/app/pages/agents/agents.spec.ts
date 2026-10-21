import { HarnessLoader } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatButtonHarness } from '@angular/material/button/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialogHarness } from '@angular/material/dialog/testing';
import { MatSnackBarHarness } from '@angular/material/snack-bar/testing';
import { Api } from '../../api/api';
import { getAgentFile } from '../../api/fn/agents/get-agent-file';
import { getAgents } from '../../api/fn/agents/get-agents';
import { revertAgentFile } from '../../api/fn/agents/revert-agent-file';
import { saveAgentFile } from '../../api/fn/agents/save-agent-file';
import { syncAgents } from '../../api/fn/agents/sync-agents';
import { AgentFileDetailResponse } from '../../api/models/agent-file-detail-response';
import { AgentFileResponse } from '../../api/models/agent-file-response';
import { AgentsResponse } from '../../api/models/agents-response';
import { HealthStore } from '../../layout/health-store';
import { FakeApi, fakePollResource, sampleHealth, settle } from '../../shared/testing';
import { Agents } from './agents';

const TEMPLATES_NOTE = 'Templates are shown read-only: a template change is a code change.';

function sampleFile(overrides: Partial<AgentFileResponse> = {}): AgentFileResponse {
  return {
    path: 'agents/review.agent.md',
    kind: 'agent',
    gitState: 'unchanged',
    gitStateLabel: 'Unchanged',
    syncState: 'in-sync',
    syncStateLabel: 'In sync',
    isEditable: true,
    ...overrides,
  };
}

const MODIFIED = sampleFile({
  path: 'agents/implement-issue.agent.md',
  gitState: 'modified',
  gitStateLabel: 'Modified',
  syncState: 'differs',
  syncStateLabel: 'Differs from ~/.copilot',
});

const SKILL = sampleFile({ path: 'skills/ui-checklist/SKILL.md', kind: 'skill' });

const TEMPLATE = sampleFile({
  path: 'templates/analysis.md',
  kind: 'template',
  syncState: 'not-installed',
  syncStateLabel: 'Not installed (read-only)',
  isEditable: false,
});

function sampleAgents(overrides: Partial<AgentsResponse> = {}): AgentsResponse {
  return {
    files: [sampleFile(), MODIFIED, SKILL, TEMPLATE],
    extra: ['agents/my-own.agent.md'],
    isOutOfSync: true,
    templatesNote: TEMPLATES_NOTE,
    ...overrides,
  };
}

function detailOf(
  file: AgentFileResponse,
  text = 'name: review\n',
  diff = '',
): AgentFileDetailResponse {
  return { file, text, hash: `hash-of-${file.path}`, diff };
}

describe('Agents', () => {
  let api: FakeApi;
  let fixture: ComponentFixture<Agents>;
  let element: HTMLElement;
  let loader: HarnessLoader;
  let root: HarnessLoader;

  async function render(): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [Agents],
      providers: [
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: HealthStore, useValue: { health: fakePollResource(sampleHealth()) } },
        { provide: Api, useValue: api },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(Agents);
    element = fixture.nativeElement as HTMLElement;
    loader = TestbedHarnessEnvironment.loader(fixture);
    root = TestbedHarnessEnvironment.documentRootLoader(fixture);
    await settle(fixture);
  }

  function texts(testId: string, scope: ParentNode = element): string[] {
    return [...scope.querySelectorAll(`[data-testid="${testId}"]`)].map(
      (node) => node.textContent?.replace(/\s+/g, ' ').trim() ?? '',
    );
  }

  function fileButton(path: string): HTMLButtonElement {
    const buttons = [...element.querySelectorAll<HTMLButtonElement>('[data-testid="agent-file"]')];
    const match = buttons.find((button) => button.textContent?.includes(path));
    if (!match) {
      throw new Error(`No file button for ${path}`);
    }

    return match;
  }

  async function open(path: string): Promise<void> {
    fileButton(path).click();
    await settle(fixture);
  }

  function editor(): HTMLTextAreaElement {
    return element.querySelector<HTMLTextAreaElement>('[data-testid="agent-editor"]')!;
  }

  async function type(text: string): Promise<void> {
    const textArea = editor();
    textArea.value = text;
    textArea.dispatchEvent(new Event('input'));
    await settle(fixture);
  }

  async function button(testId: string): Promise<MatButtonHarness> {
    return loader.getHarness(MatButtonHarness.with({ selector: `[data-testid="${testId}"]` }));
  }

  async function hasButton(testId: string): Promise<boolean> {
    return loader.hasHarness(MatButtonHarness.with({ selector: `[data-testid="${testId}"]` }));
  }

  async function snackText(): Promise<string> {
    const snackBar = await root.getHarness(MatSnackBarHarness);

    return snackBar.getMessage();
  }

  async function choose(label: string): Promise<void> {
    const dialog = await root.getHarness(MatDialogHarness);
    const choice = await dialog.getHarness(MatButtonHarness.with({ text: label }));
    await choice.click();
    await settle(fixture);
  }

  beforeEach(() => {
    api = new FakeApi()
      .answer(getAgents, () => sampleAgents())
      .answer(getAgentFile, ({ path }) => {
        const file = sampleAgents().files.find((candidate) => candidate.path === path)!;

        return detailOf(file);
      });
  });

  describe('file list', () => {
    it('groups the files by kind with their git and sync state as sent', async () => {
      await render();

      expect(texts('agent-path', element.querySelector('[data-testid="group-agent"]')!)).toEqual([
        'agents/review.agent.md',
        'agents/implement-issue.agent.md',
      ]);
      expect(texts('agent-path', element.querySelector('[data-testid="group-skill"]')!)).toEqual([
        'skills/ui-checklist/SKILL.md',
      ]);
      expect(texts('agent-path', element.querySelector('[data-testid="group-template"]')!)).toEqual(
        ['templates/analysis.md'],
      );

      const modified = fileButton('agents/implement-issue.agent.md');
      expect(modified.querySelector('[data-testid="git-state"] .label')?.textContent).toBe(
        'Modified',
      );
      expect(modified.querySelector('[data-testid="sync-state"] .label')?.textContent).toBe(
        'Differs from ~/.copilot',
      );
      expect(modified.querySelector('[data-testid="git-state"]')?.classList).toContain('tone-info');
      expect(modified.querySelector('[data-testid="sync-state"]')?.classList).toContain(
        'tone-warning',
      );
    });

    it('shows the templates note once, above the templates, and the extra files as never deleted', async () => {
      await render();

      expect(texts('templates-note')).toEqual([TEMPLATES_NOTE]);
      expect(
        element.querySelector('[data-testid="group-template"] [data-testid="templates-note"]'),
      ).not.toBeNull();
      expect(texts('agents-extra')[0]).toContain('never deletes');
      expect(texts('agents-extra')[0]).toContain('agents/my-own.agent.md');
    });

    it('shows the error state when the list cannot be read', async () => {
      api.answer(getAgents, () => {
        throw new HttpErrorResponse({ status: 0 });
      });

      await render();

      expect(element.textContent).toContain('Could not load the agent files');
      expect(element.textContent).toContain('The runner is not reachable.');
    });
  });

  describe('Sync banner', () => {
    it('is shown while out of sync and queues a sync with the runner message', async () => {
      api.answer(syncAgents, () => ({ message: 'Sync queued: it runs between jobs.' }));
      await render();

      expect(element.querySelector('[data-testid="sync-banner"]')).not.toBeNull();

      const sync = await button('sync-button');
      await sync.click();
      await settle(fixture);

      expect(api.callsOf(syncAgents)).toHaveLength(1);
      expect(await snackText()).toBe('Sync queued: it runs between jobs.');
      expect(api.callsOf(getAgents).length).toBeGreaterThan(1);
    });

    it('is hidden while every file is in sync', async () => {
      api.answer(getAgents, () => sampleAgents({ isOutOfSync: false }));
      await render();

      expect(element.querySelector('[data-testid="sync-banner"]')).toBeNull();
    });
  });

  describe('editor', () => {
    it('asks for a file until one is picked, then loads its text', async () => {
      await render();

      expect(element.textContent).toContain('No file selected');

      await open('agents/review.agent.md');

      expect(api.callsOf(getAgentFile)).toEqual([{ path: 'agents/review.agent.md' }]);
      expect(editor().value).toBe('name: review\n');
      expect(editor().readOnly).toBe(false);
    });

    it('enables Save only when the text changed', async () => {
      await render();
      await open('agents/review.agent.md');

      expect(await (await button('save-button')).isDisabled()).toBe(true);

      await type('name: review\ndescription: changed\n');
      expect(await (await button('save-button')).isDisabled()).toBe(false);

      await type('name: review\n');
      expect(await (await button('save-button')).isDisabled()).toBe(true);
    });

    it('shows a template read-only, without Save or Revert', async () => {
      await render();
      await open('templates/analysis.md');

      expect(editor().readOnly).toBe(true);
      expect(await hasButton('save-button')).toBe(false);
      expect(await hasButton('revert-button')).toBe(false);
    });

    it('saves the text with the hash it read, then reads the file again', async () => {
      api.answer(saveAgentFile, () => ({ message: 'Saved. A sync runs between jobs.' }));
      await render();
      await open('agents/review.agent.md');
      await type('name: review\ndescription: new\n');

      await (await button('save-button')).click();
      await settle(fixture);

      expect(api.callsOf(saveAgentFile)).toEqual([
        {
          path: 'agents/review.agent.md',
          body: {
            text: 'name: review\ndescription: new\n',
            baseHash: 'hash-of-agents/review.agent.md',
          },
        },
      ]);
      expect(await snackText()).toBe('Saved. A sync runs between jobs.');
      expect(api.callsOf(getAgentFile)).toHaveLength(2);
      expect(editor().value).toBe('name: review\n');
    });

    it('shows every validation reason under the editor and keeps the edits', async () => {
      api.answer(saveAgentFile, () => {
        throw new HttpErrorResponse({
          status: 400,
          error: {
            message: 'The file breaks 2 rules.',
            reasons: ['name must be "review".', 'description is empty.'],
          },
        });
      });
      await render();
      await open('agents/review.agent.md');
      await type('name: wrong\n');

      await (await button('save-button')).click();
      await settle(fixture);

      expect(texts('save-reasons')[0]).toContain('The file breaks 2 rules.');
      expect(texts('save-reason')).toEqual(['name must be "review".', 'description is empty.']);
      expect(editor().value).toBe('name: wrong\n');
      expect(api.callsOf(getAgentFile)).toHaveLength(1);
    });

    it('shows the conflict message with Reload when the file changed since it was read', async () => {
      api.answer(saveAgentFile, () => {
        throw new HttpErrorResponse({
          status: 409,
          error: { message: 'The file changed since you read it. Reload it and edit again.' },
        });
      });
      await render();
      await open('agents/review.agent.md');
      await type('name: mine\n');

      await (await button('save-button')).click();
      await settle(fixture);

      expect(texts('save-conflict')[0]).toContain('The file changed since you read it.');
      expect(await snackText()).toBe(
        'The file changed since you read it. Reload it and edit again.',
      );
      expect(editor().value).toBe('name: mine\n');

      await (await button('conflict-reload')).click();
      await choose('Discard changes');

      expect(api.callsOf(getAgentFile)).toHaveLength(2);
      expect(editor().value).toBe('name: review\n');
      expect(element.querySelector('[data-testid="save-conflict"]')).toBeNull();
    });

    it('keeps unsaved edits when the list is read again', async () => {
      api.answer(syncAgents, () => ({ message: 'Sync queued.' }));
      await render();
      await open('agents/review.agent.md');
      await type('name: mine\n');

      await (await button('sync-button')).click();
      await settle(fixture);

      expect(api.callsOf(getAgents).length).toBeGreaterThan(1);
      expect(editor().value).toBe('name: mine\n');
      expect(api.callsOf(getAgentFile)).toHaveLength(1);
    });

    it('shows the diff against git in the Diff view', async () => {
      api.answer(getAgentFile, ({ path }) =>
        detailOf(
          MODIFIED,
          'name: implement\n',
          `--- a/${path}\n+++ b/${path}\n@@ -1 +1 @@\n-name: x\n+name: implement\n`,
        ),
      );
      await render();
      await open('agents/implement-issue.agent.md');

      (element.querySelector('[data-testid="view-diff"] button') as HTMLButtonElement).click();
      await settle(fixture);

      expect(element.querySelector('.line.add')?.textContent?.trim()).toBe('+name: implement');
      expect(element.querySelector('[data-testid="agent-editor"]')).toBeNull();
    });
  });

  describe('Revert', () => {
    it('is offered only for a modified file', async () => {
      await render();
      await open('agents/review.agent.md');
      expect(await hasButton('revert-button')).toBe(false);

      await open('agents/implement-issue.agent.md');
      expect(await hasButton('revert-button')).toBe(true);
    });

    it('writes nothing when the confirmation is dismissed', async () => {
      api.answer(revertAgentFile, () => ({ message: 'Reverted.' }));
      await render();
      await open('agents/implement-issue.agent.md');

      await (await button('revert-button')).click();
      await choose('Keep changes');

      expect(api.callsOf(revertAgentFile)).toEqual([]);
    });

    it('reverts after the confirmation and reads the file again', async () => {
      api.answer(revertAgentFile, () => ({ message: 'Restored from git HEAD.' }));
      await render();
      await open('agents/implement-issue.agent.md');

      await (await button('revert-button')).click();
      const dialog = await root.getHarness(MatDialogHarness);
      expect(await dialog.getTitleText()).toBe('Revert agents/implement-issue.agent.md?');
      await choose('Revert file');

      expect(api.callsOf(revertAgentFile)).toEqual([{ path: 'agents/implement-issue.agent.md' }]);
      expect(await snackText()).toBe('Restored from git HEAD.');
      expect(api.callsOf(getAgentFile)).toHaveLength(2);
    });
  });
});
