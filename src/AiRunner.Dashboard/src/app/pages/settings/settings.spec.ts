import { HarnessLoader } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialogHarness } from '@angular/material/dialog/testing';
import { MatSnackBarHarness } from '@angular/material/snack-bar/testing';
import { MatTabGroupHarness } from '@angular/material/tabs/testing';
import { provideRouter } from '@angular/router';
import { Api } from '../../api/api';
import { getSettings } from '../../api/fn/settings/get-settings';
import { getSettingsSchema } from '../../api/fn/settings/get-settings-schema';
import { SaveSettings$Params, saveSettings } from '../../api/fn/settings/save-settings';
import { SettingDefinition } from '../../api/models/setting-definition';
import { SettingsResponse } from '../../api/models/settings-response';
import { HealthStore } from '../../layout/health-store';
import { FakeApi, fakePollResource, sampleDefinition, sampleHealth, settle } from '../../shared/testing';
import { Settings } from './settings';

function definition(overrides: Partial<SettingDefinition>): SettingDefinition {
  return sampleDefinition({ help: 'Help text.', ...overrides });
}

const SCHEMA: SettingDefinition[] = [
  definition({
    path: 'code_host.web_password',
    key: 'code_host.web_password',
    isSecret: true,
    isRestartNeeded: true,
  }),
  definition({ path: 'poll_interval_seconds', key: 'poll_interval_seconds', type: 'integer', default: 90 }),
  definition({ path: 'copilot.command', key: 'copilot.command', default: 'copilot', help: 'The Copilot CLI.' }),
  definition({ path: 'dashboard.port', key: 'dashboard.port', type: 'integer', default: 5180, isRestartNeeded: true }),
  definition({
    path: 'defaults.limits.max_diff_lines',
    key: 'limits.max_diff_lines',
    scope: 'defaults',
    type: 'integer',
    default: 500,
  }),
  definition({
    path: 'defaults.workflows',
    key: 'workflows',
    scope: 'defaults',
    type: 'string-list',
    allowedValues: ['analyze', 'implement', 'review'],
  }),
  definition({
    path: 'projects.*.limits.max_diff_lines',
    key: 'limits.max_diff_lines',
    scope: 'project',
    type: 'integer',
    default: 500,
  }),
  definition({ path: 'projects.*.commands.build', key: 'commands.build', scope: 'project' }),
  definition({ path: 'projects.*.components.*.repo', key: 'repo', scope: 'component' }),
  definition({ path: 'repos[].owner', key: 'owner', scope: 'legacy' }),
  definition({ path: 'allowed_models', key: 'allowed_models', type: 'string-list', default: [] }),
  definition({ path: 'agent_models', key: 'agent_models', type: 'string-map', default: {} }),
  definition({
    path: 'projects.*.models.implement.default',
    key: 'models.implement.default',
    scope: 'project',
  }),
  definition({
    path: 'projects.*.models.implement.fix_rounds',
    key: 'models.implement.fix_rounds',
    scope: 'project',
  }),
];

function sampleSettings(overrides: Partial<SettingsResponse> = {}): SettingsResponse {
  return {
    file: {
      code_host: { base_url: 'http://gitbucket.local', web_password: { isSet: true } },
      agent_models: { 'code-scout': 'claude-haiku-5.5' },
      defaults: { limits: { max_diff_lines: 800 }, models: { implement: { default: 'claude-sonnet-5.5' } } },
      projects: {
        shop: { limits: { max_diff_lines: 1200 }, components: { api: { repo: 'team/shop' } } },
        crm: { components: { web: { repo: 'team/crm' } } },
      },
    },
    hash: 'hash-1',
    version: 3,
    path: 'C:\\runner\\repos.yml',
    error: null,
    hasComments: false,
    restartNeeded: [],
    effective: [
      {
        project: 'shop',
        values: [
          {
            key: 'limits.max_diff_lines',
            effective: { value: 1200, source: 'project', sourceLabel: 'Set in this project' },
            inherited: { value: 800, source: 'defaults', sourceLabel: 'Inherited from defaults' },
          },
          {
            key: 'models.implement.default',
            effective: { value: 'claude-sonnet-5.5', source: 'defaults', sourceLabel: 'Inherited from defaults' },
            inherited: { value: 'claude-sonnet-5.5', source: 'defaults', sourceLabel: 'Inherited from defaults' },
          },
        ],
      },
      {
        project: 'crm',
        values: [
          {
            key: 'limits.max_diff_lines',
            effective: { value: 800, source: 'defaults', sourceLabel: 'Inherited from defaults' },
            inherited: { value: 800, source: 'defaults', sourceLabel: 'Inherited from defaults' },
          },
        ],
      },
    ],
    ...overrides,
  };
}

describe('Settings', () => {
  let api: FakeApi;
  let current: SettingsResponse;
  let fixture: ComponentFixture<Settings>;
  let element: HTMLElement;
  let loader: HarnessLoader;
  let root: HarnessLoader;

  beforeEach(() => {
    current = sampleSettings();
    api = new FakeApi()
      .answer(getSettingsSchema, () => ({ settings: SCHEMA }))
      .answer(getSettings, () => current);
  });

  async function render(): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [Settings],
      providers: [
        provideRouter([]),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: HealthStore, useValue: { health: fakePollResource(sampleHealth()) } },
        { provide: Api, useValue: api },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(Settings);
    element = fixture.nativeElement as HTMLElement;
    loader = TestbedHarnessEnvironment.loader(fixture);
    root = TestbedHarnessEnvironment.documentRootLoader(fixture);
    await settle(fixture);
  }

  function field(id: string): HTMLElement {
    const match = element.querySelector<HTMLElement>(`[data-field="${id}"]`);
    if (!match) {
      throw new Error(`No field ${id}`);
    }

    return match;
  }

  function text(node: Element | null | undefined): string {
    return node?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  }

  async function openTab(label: string): Promise<void> {
    const tabs = await loader.getHarness(MatTabGroupHarness);
    await tabs.selectTab({ label });
    await settle(fixture);
  }

  async function type(id: string, value: string): Promise<void> {
    const input = field(id).querySelector<HTMLInputElement>('[data-testid="field-input"]')!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await settle(fixture);
  }

  async function clickSave(): Promise<void> {
    element.querySelector<HTMLButtonElement>('[data-testid="save-button"]')!.click();
    await settle(fixture);
  }

  async function dialogText(): Promise<string> {
    const dialog = await root.getHarness(MatDialogHarness);
    const host = await dialog.host();

    return (await host.text()).replace(/\s+/g, ' ');
  }

  function diffLines(): string[] {
    return [...document.querySelectorAll('[data-testid="diff-line"]')].map(
      (line) => `${text(line.querySelector('[data-testid="diff-path"]'))}: ${text(line.querySelector('[data-testid="diff-value"]'))}`,
    );
  }

  async function confirmDialog(): Promise<void> {
    document.querySelector<HTMLButtonElement>('[data-testid="confirm-accept"]')!.click();
    await settle(fixture);
  }

  function saveButton(): HTMLButtonElement {
    return element.querySelector<HTMLButtonElement>('[data-testid="save-button"]')!;
  }

  it('builds the tabs and the fields from the schema', async () => {
    await render();

    const tabs = await loader.getHarness(MatTabGroupHarness);
    const labels = await Promise.all((await tabs.getTabs()).map((tab) => tab.getLabel()));

    expect(labels).toEqual(['Global', 'Defaults', 'shop', 'crm']);
    expect(text(element.querySelector('[data-testid="group-copilot"]'))).toContain('command');
    expect(text(field('copilot.command'))).toContain('The Copilot CLI.');
    expect(text(field('copilot.command').querySelector('[data-testid="field-note"]'))).toBe(
      'Built-in default: copilot',
    );
    expect(field('dashboard.port').querySelector('[data-testid="restart-chip"]')).not.toBeNull();
    expect(field('copilot.command').querySelector('[data-testid="restart-chip"]')).toBeNull();
    expect(text(field('code_host.web_password'))).toContain('Set in repos.yml');
    expect(field('code_host.web_password').querySelector('input')).toBeNull();
    expect(text(element.querySelector('[data-testid="settings-path"]'))).toBe('C:\\runner\\repos.yml');
    expect(saveButton().disabled).toBe(true);
  });

  it('shows the components of a project and the commands note', async () => {
    await render();
    await openTab('shop');

    expect(text(element.querySelector('[data-testid="component-api"]'))).toContain('api');
    expect(element.querySelector('[data-testid="commands-note"]')).not.toBeNull();
    const repoInput = field('projects.shop.components.api.repo').querySelector('input')!;
    expect(repoInput.value).toBe('team/shop');
  });

  it('shows an inherited project value, and resets an override to inherited', async () => {
    await render();
    await openTab('crm');

    const inherited = field('projects.crm.limits.max_diff_lines');
    expect(text(inherited.querySelector('[data-testid="field-note"]'))).toBe('Inherited from defaults: 800');
    expect(inherited.querySelector('[data-testid="field-reset"]')).toBeNull();

    await openTab('shop');
    const override = field('projects.shop.limits.max_diff_lines');
    expect(text(override.querySelector('[data-testid="field-note"]'))).toContain('overrides the defaults');
    expect(text(override.querySelector('[data-testid="field-reset"]'))).toBe('undo Reset to inherited');

    override.querySelector<HTMLButtonElement>('[data-testid="field-reset"]')!.click();
    await settle(fixture);

    const reset = field('projects.shop.limits.max_diff_lines');
    expect(reset.querySelector('[data-testid="field-reset"]')).toBeNull();
    expect(text(reset.querySelector('[data-testid="field-note"]'))).toBe('Inherited from defaults: 800');
    expect(saveButton().disabled).toBe(false);

    api.answer(saveSettings, () => ({ message: 'Saved and applied.', settings: current }));
    await clickSave();

    expect(diffLines()).toEqual(['projects.shop.limits.max_diff_lines: removed (was 1200)']);
    await confirmDialog();

    const [params] = api.callsOf(saveSettings) as SaveSettings$Params[];
    expect(params.body.baseHash).toBe('hash-1');
    expect((params.body.file as Record<string, unknown>)['projects']).toEqual({
      shop: { components: { api: { repo: 'team/shop' } } },
      crm: { components: { web: { repo: 'team/crm' } } },
    });
    expect((params.body.file as Record<string, unknown>)['code_host']).toEqual({
      base_url: 'http://gitbucket.local',
      web_password: { isSet: true },
    });
  });

  it('lists the changes and the comments warning before Save, and saves nothing on Keep editing', async () => {
    current = sampleSettings({ hasComments: true });
    await render();

    await type('poll_interval_seconds', '120');
    await clickSave();

    expect(diffLines()).toEqual(['poll_interval_seconds: added: 120']);
    expect(await dialogText()).toContain('repos.yml has comments');

    document.querySelector<HTMLButtonElement>('[data-testid="confirm-dismiss"]')!.click();
    await settle(fixture);

    expect(api.callsOf(saveSettings)).toEqual([]);
    expect(text(element.querySelector('[data-testid="unsaved"]'))).toBe('Unsaved changes');
  });

  it('keeps Save disabled while a number field holds text that is not a number', async () => {
    await render();

    await type('poll_interval_seconds', 'ten');

    expect(saveButton().disabled).toBe(true);
  });

  it('shows the runner message after a save and the settings it sent back', async () => {
    await render();
    await type('poll_interval_seconds', '120');

    const saved = sampleSettings({
      file: { ...(current.file as object), poll_interval_seconds: 120 },
      hash: 'hash-2',
      restartNeeded: ['dashboard.port'],
    });
    api.answer(saveSettings, () => ({ message: 'Saved and applied.', settings: saved }));
    current = saved;

    await clickSave();
    await confirmDialog();

    const snackBar = await root.getHarness(MatSnackBarHarness);
    expect(await snackBar.getMessage()).toBe('Saved and applied.');
    expect(element.querySelector('[data-testid="unsaved"]')).toBeNull();
    expect(text(element.querySelector('[data-testid="restart-needed"]'))).toContain('dashboard.port');
  });

  it('shows the resolver message of a 400 and keeps the edits', async () => {
    await render();
    await type('poll_interval_seconds', '0');
    api.answer(saveSettings, () => {
      throw new HttpErrorResponse({
        status: 400,
        error: { message: 'poll_interval_seconds must be 10 or more.' },
      });
    });

    await clickSave();
    await confirmDialog();

    expect(text(element.querySelector('[data-testid="save-error"]'))).toContain(
      'poll_interval_seconds must be 10 or more.',
    );
    expect(field('poll_interval_seconds').querySelector('input')!.value).toBe('0');
    expect(text(element.querySelector('[data-testid="unsaved"]'))).toBe('Unsaved changes');
  });

  it('says the file changed on a 409 and reloads it after asking', async () => {
    await render();
    await type('poll_interval_seconds', '120');
    api.answer(saveSettings, () => {
      throw new HttpErrorResponse({
        status: 409,
        error: { message: 'repos.yml changed since it was read. Reload it.' },
      });
    });

    await clickSave();
    await confirmDialog();

    expect(text(element.querySelector('[data-testid="save-conflict"]'))).toContain('changed since it was read');

    current = sampleSettings({ hash: 'hash-2', file: { ...(current.file as object), poll_interval_seconds: 60 } });
    element.querySelector<HTMLButtonElement>('[data-testid="conflict-reload"]')!.click();
    await settle(fixture);

    expect(await dialogText()).toContain('Discard your changes?');
    await confirmDialog();

    expect(field('poll_interval_seconds').querySelector('input')!.value).toBe('60');
    expect(element.querySelector('[data-testid="save-conflict"]')).toBeNull();
    expect(element.querySelector('[data-testid="unsaved"]')).toBeNull();
  });

  it('does not replace edits when a poll brings a new file, and offers a reload', async () => {
    await render();
    await type('poll_interval_seconds', '120');

    current = sampleSettings({ hash: 'hash-2' });
    (fixture.componentInstance as unknown as { settings: { reload(): void } }).settings.reload();
    await settle(fixture);

    expect(field('poll_interval_seconds').querySelector('input')!.value).toBe('120');
    expect(element.querySelector('[data-testid="changed-on-disk"]')).not.toBeNull();
  });

  it('shows the file error, the restart list and the legacy note', async () => {
    current = sampleSettings({
      error: 'repos.yml line 4: unknown key "polll".',
      restartNeeded: ['paths.work', 'dashboard.port'],
      file: { git_bucket: { base_url: 'http://gitbucket.local' }, repos: [{ owner: 'team', name: 'shop' }] },
      effective: [],
    });
    await render();

    expect(text(element.querySelector('[data-testid="config-error"]'))).toContain('unknown key "polll"');
    expect(text(element.querySelector('[data-testid="restart-needed"]'))).toContain('paths.work, dashboard.port');
    expect(element.querySelector('[data-testid="legacy-note"]')).not.toBeNull();
  });

  it('edits a string-map with add, change and remove lines', async () => {
    await render();

    const map = () => field('agent_models');
    const names = () =>
      [...map().querySelectorAll<HTMLInputElement>('[data-testid="map-name"]')].map((input) => input.value);
    expect(names()).toEqual(['code-scout']);
    expect(map().querySelector<HTMLInputElement>('[data-testid="map-value"]')!.value).toBe('claude-haiku-5.5');

    map().querySelector<HTMLButtonElement>('[data-testid="map-add"]')!.click();
    await settle(fixture);
    expect(names()).toEqual(['code-scout', '']);
    // A line without a name is not in the file yet.
    expect(saveButton().disabled).toBe(true);

    const value = map().querySelectorAll<HTMLInputElement>('[data-testid="map-value"]')[1];
    value.value = 'claude-sonnet-5.5';
    value.dispatchEvent(new Event('change'));
    await settle(fixture);
    const name = map().querySelectorAll<HTMLInputElement>('[data-testid="map-name"]')[1];
    name.value = 'code-reviewer';
    name.dispatchEvent(new Event('change'));
    await settle(fixture);

    map().querySelector<HTMLButtonElement>('[data-testid="map-remove"]')!.click();
    await settle(fixture);
    expect(names()).toEqual(['code-reviewer']);

    api.answer(saveSettings, () => ({ message: 'Saved and applied.', settings: current }));
    await clickSave();

    expect(diffLines()).toEqual([
      'agent_models.code-scout: removed (was claude-haiku-5.5)',
      'agent_models.code-reviewer: added: claude-sonnet-5.5',
    ]);
    await confirmDialog();

    const [params] = api.callsOf(saveSettings) as SaveSettings$Params[];
    expect((params.body.file as Record<string, unknown>)['agent_models']).toEqual({
      'code-reviewer': 'claude-sonnet-5.5',
    });
  });

  it('shows the model keys of a workflow under its heading, with the inherited value', async () => {
    await render();
    await openTab('shop');

    const models = element.querySelector('[data-testid="group-models"]')!;
    expect(text(models.querySelector('[data-testid="section-models-implement"]'))).toBe('implement');
    const own = field('projects.shop.models.implement.default');
    expect(text(own.querySelector('.key'))).toBe('default');
    expect(text(own.querySelector('[data-testid="field-note"]'))).toBe(
      'Inherited from defaults: claude-sonnet-5.5',
    );

    await type('projects.shop.models.implement.default', 'claude-opus-5.5');
    api.answer(saveSettings, () => ({ message: 'Saved and applied.', settings: current }));
    await clickSave();

    expect(diffLines()).toEqual(['projects.shop.models.implement.default: added: claude-opus-5.5']);
  });

  it('shows the error state when the runner sends no file', async () => {
    current = sampleSettings({ file: null, error: 'This dashboard has no runner, so no repos.yml to edit.' });
    await render();

    expect(text(element)).toContain('No repos.yml to edit');
    expect(text(element)).toContain('no repos.yml to edit');
    expect(element.querySelector('[data-testid="save-button"]')).toBeNull();
  });
});
