import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Api } from '../../api/api';
import { deleteOrphanWorkspace } from '../../api/fn/workspaces/delete-orphan-workspace';
import { fetchWorkspace } from '../../api/fn/workspaces/fetch-workspace';
import { getWorkspaces } from '../../api/fn/workspaces/get-workspaces';
import { recloneWorkspace } from '../../api/fn/workspaces/reclone-workspace';
import { CloneResponse } from '../../api/models/clone-response';
import { OrphanResponse } from '../../api/models/orphan-response';
import { ProjectWorkspaceResponse } from '../../api/models/project-workspace-response';
import { BytesPipe } from '../../shared/bytes.pipe';
import { confirmWrite } from '../../shared/confirm-dialog/confirm-dialog';
import { DataCell, DataColumn, DataTable } from '../../shared/data-table/data-table';
import { InlineNote } from '../../shared/inline-note/inline-note';
import { PageHeader } from '../../shared/page-header/page-header';
import { pollResource } from '../../shared/poll-resource';
import { StatusChip } from '../../shared/status-chip/status-chip';
import { AppTime } from '../../shared/time/time';
import { workspaceWarningIcon } from '../../shared/tone';
import { ViewState } from '../../shared/view-state/view-state';
import { writeAction } from '../../shared/write-action';

export const BUSY_TOOLTIP = 'A job is using this project';

/**
 * The clones the runner keeps under `work/<project>/`, one card per project, and the folders of `work/` that belong
 * to no project. Everything shown comes from `GET /api/workspaces` (local git only, so it is cheap to poll). Fetch,
 * Re-clone and Delete go to the runner, which refuses with its own message while a job uses the project.
 */
@Component({
  selector: 'app-workspaces',
  imports: [
    AppTime,
    BytesPipe,
    DataCell,
    DataTable,
    InlineNote,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    PageHeader,
    StatusChip,
    ViewState,
  ],
  templateUrl: './workspaces.html',
  styleUrl: './workspaces.scss',
})
export class Workspaces {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);

  protected readonly workspaces = pollResource(() => this.api.invoke(getWorkspaces));

  /**
   * One group for every clone button (Fetch and Re-clone): while one call runs they are all disabled, and the key of
   * the clicked button shows the spinner.
   */
  protected readonly cloneWrite = writeAction({
    failure: 'The runner did not change the workspace.',
    after: () => this.workspaces.reload(),
  });
  protected readonly busyCloneKey = signal<string | undefined>(undefined);

  protected readonly orphanDelete = writeAction({
    failure: 'The runner did not delete the folder.',
    after: () => this.workspaces.reload(),
  });
  protected readonly busyOrphan = signal<string | undefined>(undefined);

  protected readonly busyTooltip = BUSY_TOOLTIP;
  protected readonly warningIcon = workspaceWarningIcon;

  protected readonly cloneColumns: readonly DataColumn[] = [
    { key: 'repo', header: 'Repository' },
    { key: 'checkout', header: 'Checked out' },
    { key: 'head', header: 'Last commit', hideBelow: 'sm' },
    { key: 'lastFetchAt', header: 'Last fetch', hideBelow: 'md' },
    { key: 'changes', header: 'Changed files', align: 'end', hideBelow: 'md' },
    { key: 'size', header: 'Size', align: 'end', hideBelow: 'sm' },
    { key: 'actions', header: 'Actions', align: 'end' },
  ];

  protected readonly orphanColumns: readonly DataColumn[] = [
    { key: 'folder', header: 'Folder' },
    { key: 'size', header: 'Size', align: 'end' },
    { key: 'actions', header: 'Actions', align: 'end' },
  ];

  protected readonly trackByFolder = (_index: number, row: CloneResponse | OrphanResponse) =>
    row.folder;

  /** The key of one clone button: action, project and folder. */
  protected cloneKey(
    action: string,
    project: ProjectWorkspaceResponse,
    clone: CloneResponse,
  ): string {
    return `${action}:${project.name}/${clone.folder}`;
  }

  /** The changed paths one per line, for the tooltip of the count. */
  protected changedList(clone: CloneResponse): string {
    return clone.changedPaths.join('\n');
  }

  protected fetch(project: ProjectWorkspaceResponse, clone: CloneResponse): void {
    this.busyCloneKey.set(this.cloneKey('fetch', project, clone));
    void this.cloneWrite.run(() =>
      this.api.invoke(fetchWorkspace, { project: project.name, repo: clone.folder }),
    );
  }

  protected async reclone(project: ProjectWorkspaceResponse, clone: CloneResponse): Promise<void> {
    const isConfirmed = await confirmWrite(this.dialog, {
      title: `Re-clone ${clone.repo}?`,
      message:
        `The runner deletes the folder ${clone.folder} of project ${project.name}, with every change in it. ` +
        'The next job of this project clones the repository again.',
      confirmLabel: 'Re-clone',
      dismissLabel: 'Keep clone',
    });

    if (!isConfirmed) {
      return;
    }

    this.busyCloneKey.set(this.cloneKey('reclone', project, clone));
    await this.cloneWrite.run(() =>
      this.api.invoke(recloneWorkspace, { project: project.name, repo: clone.folder }),
    );
  }

  protected async deleteOrphan(orphan: OrphanResponse): Promise<void> {
    const isConfirmed = await confirmWrite(this.dialog, {
      title: `Delete the folder ${orphan.folder}?`,
      message:
        `The runner deletes work/${orphan.folder} and everything in it. No project of repos.yml uses it; ` +
        'this cannot be undone.',
      confirmLabel: 'Delete folder',
      dismissLabel: 'Keep folder',
    });

    if (!isConfirmed) {
      return;
    }

    this.busyOrphan.set(orphan.folder);
    await this.orphanDelete.run(() =>
      this.api.invoke(deleteOrphanWorkspace, { folder: orphan.folder }),
    );
  }
}
