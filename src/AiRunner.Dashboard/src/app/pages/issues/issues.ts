import { Component, computed, inject, signal } from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { RouterLink } from '@angular/router';
import { Api } from '../../api/api';
import { getIssues } from '../../api/fn/issues/get-issues';
import { IssueRowResponse } from '../../api/models/issue-row-response';
import { DataCell, DataColumn, DataTable } from '../../shared/data-table/data-table';
import { issueLink } from '../../shared/issue-link';
import { PageHeader } from '../../shared/page-header/page-header';
import { pollResource } from '../../shared/poll-resource';
import { StageChip } from '../../shared/stage-chip/stage-chip';
import { StatusChip } from '../../shared/status-chip/status-chip';
import { AppTime } from '../../shared/time/time';
import { issueMarkIcon, issueMarkTone } from '../../shared/tone';
import { ISSUE_STATE_OPTIONS } from './issue-filters';

/** One line of the table: an issue, or a sub-issue shown indented right under its parent. */
export interface IssueTableRow extends IssueRowResponse {
  isSubIssue: boolean;
}

/**
 * Every issue the runner polls, with the stage, flags and badge the API computed. Sub-issues are nested under their
 * parent unless the flat list is on; the filters are sent to the API, which does the matching.
 */
@Component({
  selector: 'app-issues',
  imports: [
    AppTime,
    DataCell,
    DataTable,
    MatFormFieldModule,
    MatIconModule,
    MatSelectModule,
    MatSlideToggleModule,
    PageHeader,
    RouterLink,
    StageChip,
    StatusChip,
  ],
  templateUrl: './issues.html',
  styleUrl: './issues.scss',
})
export class Issues {
  private readonly api = inject(Api);

  protected readonly stateOptions = ISSUE_STATE_OPTIONS;

  /** Empty means "all". */
  protected readonly project = signal('');
  protected readonly component = signal('');
  protected readonly stage = signal('');
  protected readonly state = signal('');
  protected readonly flat = signal(false);

  protected readonly issues = pollResource(() =>
    this.api.invoke(getIssues, {
      project: this.project() || undefined,
      component: this.component() || undefined,
      stage: this.stage() || undefined,
      state: this.state() || undefined,
      flat: this.flat() || undefined,
    }),
  );

  protected readonly projectOptions = computed(() => this.issues.value()?.projects ?? []);
  protected readonly stageOptions = computed(() => this.issues.value()?.stages ?? []);

  /** The components of the chosen project, or of every project; a name shared by two projects is listed once. */
  protected readonly componentOptions = computed(() => {
    const chosen = this.project();
    const projects = this.projectOptions().filter((option) => !chosen || option.name === chosen);
    const names = projects.flatMap((option) => option.components);

    return [...new Set(names)];
  });

  /** The nested answer as table lines: each parent followed by its sub-issues. */
  protected readonly rows = computed<IssueTableRow[] | undefined>(() =>
    this.issues.value()?.items.flatMap((item) => [
      { ...item, isSubIssue: false },
      ...item.subIssues.map((subIssue) => ({ ...subIssue, isSubIssue: true })),
    ]),
  );

  protected readonly columns: readonly DataColumn[] = [
    { key: 'issue', header: 'Issue' },
    { key: 'stage', header: 'Stage', width: '190px' },
    { key: 'flags', header: 'Flags', hideBelow: 'sm' },
    { key: 'component', header: 'Component', width: '130px', hideBelow: 'md' },
    { key: 'state', header: 'State', width: '90px', hideBelow: 'sm' },
    { key: 'updatedAt', header: 'Updated', width: '170px', hideBelow: 'md' },
  ];

  protected readonly link = (row: IssueRowResponse) => issueLink(row.repo, row.number);
  protected readonly markTone = issueMarkTone;
  protected readonly markIcon = issueMarkIcon;
  protected readonly trackByIssue = (_index: number, row: IssueTableRow) => `${row.repo}#${row.number}`;

  protected setProject(name: string): void {
    this.project.set(name);
    // A component of another project would match nothing (and the API refuses it).
    this.component.set('');
    this.issues.reload();
  }

  protected setComponent(name: string): void {
    this.component.set(name);
    this.issues.reload();
  }

  protected setStage(code: string): void {
    this.stage.set(code);
    this.issues.reload();
  }

  protected setState(code: string): void {
    this.state.set(code);
    this.issues.reload();
  }

  protected setFlat(isFlat: boolean): void {
    this.flat.set(isFlat);
    this.issues.reload();
  }
}
