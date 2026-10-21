import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Api } from '../../api/api';
import { getAgentFile } from '../../api/fn/agents/get-agent-file';
import { getAgents } from '../../api/fn/agents/get-agents';
import { revertAgentFile } from '../../api/fn/agents/revert-agent-file';
import { saveAgentFile } from '../../api/fn/agents/save-agent-file';
import { syncAgents } from '../../api/fn/agents/sync-agents';
import { AgentFileDetailResponse } from '../../api/models/agent-file-detail-response';
import { AgentFileResponse } from '../../api/models/agent-file-response';
import { HealthStore } from '../../layout/health-store';
import { confirmWrite } from '../../shared/confirm-dialog/confirm-dialog';
import { InlineNote } from '../../shared/inline-note/inline-note';
import { PageHeader } from '../../shared/page-header/page-header';
import { describeError, pollResource } from '../../shared/poll-resource';
import { StatusChip } from '../../shared/status-chip/status-chip';
import { agentStateTone } from '../../shared/tone';
import { ViewState } from '../../shared/view-state/view-state';
import { writeAction } from '../../shared/write-action';
import { AgentDiff } from './agent-diff';

/** One group of the file list: the API's `kind` code and the heading the screen shows for it. */
export interface AgentKindGroup {
  kind: string;
  heading: string;
  /** Show the API's `templatesNote` once above this group. */
  hasTemplatesNote: boolean;
}

/** The groups of the file list, in order. A file of a kind not listed here is not shown (none exists today). */
export const AGENT_KIND_GROUPS: readonly AgentKindGroup[] = [
  { kind: 'agent', heading: 'Agents', hasTemplatesNote: false },
  { kind: 'skill', heading: 'Skills', hasTemplatesNote: false },
  { kind: 'template', heading: 'Templates', hasTemplatesNote: true },
];

/**
 * Git state codes that offer Revert (restore from git HEAD). The task offers it only for `modified`; the API would
 * ideally send this as a `canRevert` flag per file.
 */
const REVERTIBLE_GIT_STATES: ReadonlySet<string> = new Set(['modified']);

/** The two views of the selected file. */
type FileView = 'edit' | 'diff';

/**
 * The shared agents, skills and templates the runner installs to `~/.copilot`: their git and sync state, an editor
 * (a plain monospace text box) with the runner's validation reasons under it, the diff against git, Save, Revert and
 * a Sync banner while Copilot's copy differs. The list is polled; the open file is read once when selected and again
 * only after a write or a Reload, so a poll never replaces unsaved text.
 */
@Component({
  selector: 'app-agents',
  imports: [
    AgentDiff,
    InlineNote,
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    MatProgressSpinnerModule,
    PageHeader,
    StatusChip,
    ViewState,
  ],
  templateUrl: './agents.html',
  styleUrl: './agents.scss',
})
export class Agents {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);
  private readonly health = inject(HealthStore).health;

  protected readonly agents = pollResource(() => this.api.invoke(getAgents));

  protected readonly groups = computed(() => {
    const files = this.agents.value()?.files ?? [];

    return AGENT_KIND_GROUPS.map((group) => ({
      ...group,
      files: files.filter((file) => file.kind === group.kind),
    })).filter((group) => group.files.length > 0);
  });

  /** The path the person picked; the file's text is in `detail`. */
  protected readonly selectedPath = signal<string | undefined>(undefined);
  protected readonly detail = signal<AgentFileDetailResponse | undefined>(undefined);
  protected readonly detailError = signal<string | undefined>(undefined);
  protected readonly isDetailLoading = signal(false);
  /** The text in the editor; differs from `detail().text` while there are unsaved edits. */
  protected readonly draft = signal('');
  protected readonly view = signal<FileView>('edit');

  /** The runner's refusal of the last save: its message and every reason (a 400). */
  protected readonly validation = signal<{ message: string; reasons: string[] } | undefined>(
    undefined,
  );
  /** The runner's message when the file changed since it was read (a 409): the person must reload. */
  protected readonly conflict = signal<string | undefined>(undefined);

  /** The selected file's row from the polled list (fresher states), else the one read with the file. */
  protected readonly selectedFile = computed<AgentFileResponse | undefined>(() => {
    const path = this.selectedPath();
    const fromList = this.agents.value()?.files.find((file) => file.path === path);

    return fromList ?? this.detail()?.file;
  });

  protected readonly isDirty = computed(() => {
    const detail = this.detail();

    return detail !== undefined && this.draft() !== detail.text;
  });

  protected readonly isEditable = computed(() => this.detail()?.file.isEditable === true);

  protected readonly canSave = computed(
    () => this.isEditable() && this.isDirty() && !this.saveWrite.isBusy(),
  );

  protected readonly canRevert = computed(() => {
    const file = this.selectedFile();

    return file !== undefined && file.isEditable && REVERTIBLE_GIT_STATES.has(file.gitState);
  });

  protected readonly stateTone = agentStateTone;

  protected readonly syncWrite = writeAction({
    failure: 'The runner did not queue a sync.',
    after: () => this.reloadListAndHealth(),
  });

  protected readonly saveWrite = writeAction({
    failure: 'The runner did not save the file.',
    after: () => this.reloadListAndHealth(),
  });

  protected readonly revertWrite = writeAction({
    failure: 'The runner did not revert the file.',
    after: () => this.reloadListAndHealth(),
  });

  /** Counts reads of the file, so an older answer never replaces a newer one. */
  private readSequence = 0;

  protected async select(file: AgentFileResponse): Promise<void> {
    if (file.path === this.selectedPath()) {
      return;
    }

    const canLeave = await this.confirmDiscard();
    if (!canLeave) {
      return;
    }

    this.selectedPath.set(file.path);
    this.detail.set(undefined);
    this.view.set('edit');
    await this.load(file.path);
  }

  protected async reload(): Promise<void> {
    const path = this.selectedPath();
    if (path === undefined) {
      return;
    }

    const canLeave = await this.confirmDiscard();
    if (!canLeave) {
      return;
    }

    await this.load(path);
  }

  protected onInput(event: Event): void {
    const textArea = event.target as HTMLTextAreaElement;
    this.draft.set(textArea.value);
  }

  protected sync(): void {
    void this.syncWrite.run(() => this.api.invoke(syncAgents));
  }

  protected async save(): Promise<void> {
    const detail = this.detail();
    if (detail === undefined || !this.canSave()) {
      return;
    }

    const path = detail.file.path;
    const body = { text: this.draft(), baseHash: detail.hash };
    this.validation.set(undefined);
    this.conflict.set(undefined);

    const isSaved = await this.saveWrite.run(async () => {
      try {
        return await this.api.invoke(saveAgentFile, { path, body });
      } catch (failure) {
        this.noteSaveFailure(failure);
        throw failure;
      }
    });

    if (isSaved) {
      await this.load(path);
    }
  }

  protected async revert(): Promise<void> {
    const file = this.selectedFile();
    if (file === undefined) {
      return;
    }

    const isConfirmed = await confirmWrite(this.dialog, {
      title: `Revert ${file.path}?`,
      message:
        'The runner restores the file from git HEAD. Every change to it that is not committed, saved or not, is ' +
        'lost; a sync then copies the restored file to ~/.copilot.',
      confirmLabel: 'Revert file',
      dismissLabel: 'Keep changes',
    });

    if (!isConfirmed) {
      return;
    }

    const path = file.path;
    const isReverted = await this.revertWrite.run(() => this.api.invoke(revertAgentFile, { path }));

    if (isReverted) {
      await this.load(path);
    }
  }

  /** Reads the file once and puts its text in the editor (unsaved edits are gone; callers ask first). */
  private async load(path: string): Promise<void> {
    const sequence = ++this.readSequence;
    this.isDetailLoading.set(true);
    this.detailError.set(undefined);

    try {
      const detail = await this.api.invoke(getAgentFile, { path });

      if (sequence === this.readSequence) {
        this.detail.set(detail);
        this.draft.set(detail.text);
        this.validation.set(undefined);
        this.conflict.set(undefined);
      }
    } catch (failure) {
      if (sequence === this.readSequence) {
        this.detailError.set(describeError(failure));
      }
    } finally {
      if (sequence === this.readSequence) {
        this.isDetailLoading.set(false);
      }
    }
  }

  /** True when there is nothing unsaved, or the person agreed to drop it. */
  private async confirmDiscard(): Promise<boolean> {
    if (!this.isDirty()) {
      return true;
    }

    return confirmWrite(this.dialog, {
      title: 'Discard your changes?',
      message: `Your edits to ${this.detail()?.file.path} are not saved. They are lost when the file is read again.`,
      confirmLabel: 'Discard changes',
      dismissLabel: 'Keep editing',
    });
  }

  /**
   * Keeps the runner's refusal next to the editor: the reasons of a 400 under it, the message of a 409 with a
   * Reload button. The snack bar shows the message as for every write.
   */
  private noteSaveFailure(failure: unknown): void {
    if (!(failure instanceof HttpErrorResponse)) {
      return;
    }

    const message = describeError(failure, 'The runner did not save the file.');

    if (failure.status === 409) {
      this.conflict.set(message);

      return;
    }

    const reasons = (failure.error as { reasons?: unknown } | null)?.reasons;
    if (failure.status === 400) {
      const reasonList = Array.isArray(reasons)
        ? reasons.filter((reason) => typeof reason === 'string')
        : [];
      this.validation.set({ message, reasons: reasonList });
    }
  }

  private reloadListAndHealth(): void {
    this.agents.reload();
    this.health.reload();
  }
}
