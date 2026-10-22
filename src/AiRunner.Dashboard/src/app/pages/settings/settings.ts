import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTabsModule } from '@angular/material/tabs';
import { Api } from '../../api/api';
import { getSettings } from '../../api/fn/settings/get-settings';
import { getSettingsSchema } from '../../api/fn/settings/get-settings-schema';
import { saveSettings } from '../../api/fn/settings/save-settings';
import { EffectiveValue } from '../../api/models/effective-value';
import { JsonObject } from '../../api/models/json-object';
import { SettingsResponse } from '../../api/models/settings-response';
import { HealthStore } from '../../layout/health-store';
import { confirmWrite } from '../../shared/confirm-dialog/confirm-dialog';
import { InlineNote } from '../../shared/inline-note/inline-note';
import { PageHeader } from '../../shared/page-header/page-header';
import { describeError, pollResource } from '../../shared/poll-resource';
import { ViewState } from '../../shared/view-state/view-state';
import { saveRefusal, writeAction } from '../../shared/write-action';
import { SettingFieldEditor } from './setting-field';
import { confirmSettingsSave } from './settings-diff-dialog';
import {
  JsonMap,
  JsonValue,
  SettingField,
  SettingGroup,
  buildGroups,
  cloneFile,
  diffSettings,
  isMap,
  namesAt,
  valueAt,
  withValue,
  withoutValue,
} from './settings-form';

/** The schema never changes while the runner runs; it is read again only now and then. */
const SCHEMA_POLL_MS = 60_000;

/** The group whose keys run commands on this machine: it gets a short note. */
const COMMANDS_GROUP = 'commands';

/** One component of a project tab and its fields. */
interface ComponentForm {
  name: string;
  groups: SettingGroup[];
}

/** One tab: Global, Defaults or a project (with its components). */
interface SettingsTab {
  id: string;
  label: string;
  /** The project's name on a project tab: its keys get the API's effective values. */
  project?: string;
  groups: SettingGroup[];
  components: ComponentForm[];
}

/**
 * repos.yml as forms built from the settings schema: tabs Global, Defaults and one per project (components inside),
 * the inherited value of every project key that the project does not set (from the API's `effective`), a reset to
 * inherited, and the list of changes before Save. The file is polled only while nothing is edited, so a poll never
 * replaces unsaved edits; a change on disk during an edit is announced with a Reload button instead.
 */
@Component({
  selector: 'app-settings',
  imports: [
    InlineNote,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTabsModule,
    NgTemplateOutlet,
    PageHeader,
    SettingFieldEditor,
    ViewState,
  ],
  templateUrl: './settings.html',
  styleUrl: './settings.scss',
})
export class Settings {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);
  private readonly health = inject(HealthStore).health;

  protected readonly schema = pollResource(() => this.api.invoke(getSettingsSchema), SCHEMA_POLL_MS);
  protected readonly settings = pollResource(() => this.api.invoke(getSettings));

  /** The settings the form was built from: the base of the draft, the diff and the hash a save sends. */
  protected readonly loaded = signal<SettingsResponse | undefined>(undefined);
  /** The edited file. */
  protected readonly draft = signal<JsonMap>({});
  /** Ids of the fields whose typed text is not a valid value: Save waits until they are fixed. */
  protected readonly invalidFields = signal<ReadonlySet<string>>(new Set());

  /** The runner's refusal of the last save (a 400): the resolver's message. */
  protected readonly saveError = signal<string | undefined>(undefined);
  /** The runner's message when repos.yml changed since it was read (a 409). */
  protected readonly conflict = signal<string | undefined>(undefined);
  /** Why the last Reload failed. */
  protected readonly reloadError = signal<string | undefined>(undefined);

  protected readonly changes = computed(() => {
    const loaded = this.loaded();
    if (loaded === undefined) {
      return [];
    }

    // diffSettings only reads: the loaded file needs no copy.
    return diffSettings(loaded.file as JsonValue, this.draft());
  });

  protected readonly isDirty = computed(() => this.changes().length > 0);
  protected readonly hasInvalid = computed(() => this.invalidFields().size > 0);

  protected readonly canSave = computed(
    () => this.isDirty() && !this.hasInvalid() && !this.saveWrite.isBusy(),
  );

  /** The newest answer: the banners (error, restart needed) follow it even while the form keeps older data. */
  protected readonly latest = computed(() => this.settings.value() ?? this.loaded());

  /** repos.yml changed (hand edit, another tab) after the form was built, and the form holds edits. */
  protected readonly hasChangedOnDisk = computed(() => {
    const latest = this.settings.value();
    const loaded = this.loaded();

    return latest !== undefined && loaded !== undefined && latest.hash !== loaded.hash && this.isDirty();
  });

  /** The file uses the old `git_bucket:` block or the flat `repos:` list, which are edited by hand. */
  protected readonly isLegacy = computed(() => {
    const file = this.loaded()?.file as JsonValue | undefined;

    return isMap(file) && ('repos' in file || 'git_bucket' in file);
  });

  protected readonly tabs = computed<SettingsTab[]>(() => {
    const schema = this.schema.value()?.settings ?? [];
    const file = this.loaded()?.file as JsonValue | undefined;

    const tabs: SettingsTab[] = [
      { id: 'global', label: 'Global', groups: buildGroups(schema, { scope: 'global', root: [] }), components: [] },
      {
        id: 'defaults',
        label: 'Defaults',
        groups: buildGroups(schema, { scope: 'defaults', root: ['defaults'] }),
        components: [],
      },
    ];

    for (const project of namesAt(file, ['projects'])) {
      const root = ['projects', project];
      const components = namesAt(file, [...root, 'components']).map((name) => ({
        name,
        groups: buildGroups(schema, { scope: 'component', root: [...root, 'components', name] }),
      }));

      tabs.push({
        id: `project-${project}`,
        label: project,
        project,
        groups: buildGroups(schema, { scope: 'project', root }),
        components,
      });
    }

    return tabs;
  });

  /** The API's merged values, per project and key. */
  private readonly effectiveByProject = computed(() => {
    const byProject = new Map<string, Map<string, EffectiveValue>>();

    for (const project of this.loaded()?.effective ?? []) {
      byProject.set(project.project, new Map(project.values.map((value) => [value.key, value])));
    }

    return byProject;
  });

  protected readonly commandsGroup = COMMANDS_GROUP;

  protected readonly saveWrite = writeAction({
    failure: 'The runner did not save the settings.',
    after: () => {
      this.settings.reload();
      this.health.reload();
    },
  });

  constructor() {
    // A poll answer replaces the form only while nothing is edited and only when repos.yml changed (another hash or
    // version), so an idle page does not rebuild every form on each poll; the first answer always builds it.
    effect(() => {
      const next = this.settings.value();
      if (next === undefined) {
        return;
      }

      untracked(() => {
        const loaded = this.loaded();
        if (loaded === undefined) {
          this.adopt(next);

          return;
        }

        const isNewFile = next.hash !== loaded.hash || next.version !== loaded.version;
        if (isNewFile && !this.isDirty()) {
          this.adopt(next);
        }
      });
    });
  }

  protected valueOf(field: SettingField): JsonValue | undefined {
    return valueAt(this.draft(), field.segments);
  }

  protected effectiveOf(project: string | undefined, field: SettingField): EffectiveValue | undefined {
    if (project === undefined || field.definition.scope !== 'project') {
      return undefined;
    }

    return this.effectiveByProject().get(project)?.get(field.definition.key);
  }

  protected setValue(field: SettingField, value: JsonValue | undefined): void {
    if (value === undefined) {
      const keep = field.segments.length - field.definition.key.split('.').length;
      this.draft.update((draft) => withoutValue(draft, field.segments, keep));

      return;
    }

    this.draft.update((draft) => withValue(draft, field.segments, value));
  }

  protected setInvalid(field: SettingField, isInvalid: boolean): void {
    this.invalidFields.update((current) => {
      const next = new Set(current);
      if (isInvalid) {
        next.add(field.id);
      } else {
        next.delete(field.id);
      }

      return next;
    });
  }

  protected async save(): Promise<void> {
    const loaded = this.loaded();
    if (loaded === undefined || !this.canSave()) {
      return;
    }

    const isConfirmed = await confirmSettingsSave(this.dialog, {
      changes: this.changes(),
      hasComments: this.latest()?.hasComments === true,
      path: loaded.path,
    });

    if (!isConfirmed) {
      return;
    }

    const body = { file: this.draft() as JsonObject, baseHash: loaded.hash };
    this.saveError.set(undefined);
    this.conflict.set(undefined);

    await this.saveWrite.run(async () => {
      try {
        const answer = await this.api.invoke(saveSettings, { body });
        this.adopt(answer.settings);

        return answer;
      } catch (failure) {
        this.noteSaveFailure(failure);
        throw failure;
      }
    });
  }

  /** Reads repos.yml again and rebuilds the form from it (edits are lost: asks first). */
  protected async reload(): Promise<void> {
    const canLeave = await this.confirmDiscard();
    if (!canLeave) {
      return;
    }

    this.reloadError.set(undefined);

    try {
      const fresh = await this.api.invoke(getSettings);
      this.adopt(fresh);
    } catch (failure) {
      this.reloadError.set(describeError(failure, 'Could not read repos.yml again.'));
    }
  }

  protected async discard(): Promise<void> {
    const loaded = this.loaded();
    const canLeave = await this.confirmDiscard();

    if (loaded !== undefined && canLeave) {
      this.adopt(loaded);
    }
  }

  /** Builds the form from an answer of the runner: the draft is its file again, nothing is unsaved. */
  private adopt(settings: SettingsResponse): void {
    this.loaded.set(settings);
    this.draft.set(cloneFile(settings.file));
    this.invalidFields.set(new Set());
    this.saveError.set(undefined);
    this.conflict.set(undefined);
  }

  /** True when there is nothing unsaved, or the person agreed to drop it. */
  private async confirmDiscard(): Promise<boolean> {
    if (!this.isDirty()) {
      return true;
    }

    return confirmWrite(this.dialog, {
      title: 'Discard your changes?',
      message: 'Your edits to the settings are not saved. They are lost when repos.yml is read again.',
      confirmLabel: 'Discard changes',
      dismissLabel: 'Keep editing',
    });
  }

  /** Keeps the runner's refusal next to the form: the resolver's message of a 400, the message of a 409. */
  private noteSaveFailure(failure: unknown): void {
    const refusal = saveRefusal(failure, 'The runner did not save the settings.');

    if (refusal?.kind === 'conflict') {
      this.conflict.set(refusal.message);
    } else if (refusal?.kind === 'invalid') {
      this.saveError.set(refusal.message);
    }
  }
}
