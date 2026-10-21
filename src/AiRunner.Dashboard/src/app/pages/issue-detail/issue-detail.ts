import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Api } from '../../api/api';
import { getIssue } from '../../api/fn/issues/get-issue';
import { JobResponse } from '../../api/models/job-response';
import { PullResponse } from '../../api/models/pull-response';
import { IssueRowResponse } from '../../api/models/issue-row-response';
import { ActionButtons, ActionTarget } from '../../shared/action-buttons/action-buttons';
import { DataCell, DataColumn, DataTable } from '../../shared/data-table/data-table';
import { InlineNote } from '../../shared/inline-note/inline-note';
import { issueKey, issueLink } from '../../shared/issue-link';
import { PageHeader } from '../../shared/page-header/page-header';
import { PullLink } from '../../shared/pull-link/pull-link';
import { pollResource } from '../../shared/poll-resource';
import { StageChip } from '../../shared/stage-chip/stage-chip';
import { StatusChip } from '../../shared/status-chip/status-chip';
import { DurationPipe } from '../../shared/time/duration.pipe';
import { AppTime } from '../../shared/time/time';
import { issueMarkIcon, issueMarkTone, jobStatusTone } from '../../shared/tone';
import { ViewState } from '../../shared/view-state/view-state';

/**
 * One issue end to end: stage, what waits on a person, the analysis summary, sub-issues, pull requests and the job
 * timeline. Everything shown was computed by the API; this page only lays it out.
 */
@Component({
  selector: 'app-issue-detail',
  imports: [
    ActionButtons,
    AppTime,
    DataCell,
    DataTable,
    DurationPipe,
    InlineNote,
    MatButtonModule,
    MatIconModule,
    PageHeader,
    PullLink,
    RouterLink,
    StageChip,
    StatusChip,
    ViewState,
  ],
  templateUrl: './issue-detail.html',
  styleUrl: './issue-detail.scss',
})
export class IssueDetail {
  private readonly api = inject(Api);

  // Read from the route synchronously (paramMap replays its value), so the first poll already has the issue.
  private readonly params = toSignal(inject(ActivatedRoute).paramMap, { requireSync: true });

  protected readonly owner = computed(() => this.params().get('owner') ?? '');
  protected readonly name = computed(() => this.params().get('repo') ?? '');
  protected readonly number = computed(() => Number(this.params().get('number')));

  protected readonly detail = pollResource(() =>
    this.api.invoke(getIssue, { owner: this.owner(), repo: this.name(), number: this.number() }),
  );

  /** The API answers 404 with no body; the failure text then carries the status. */
  protected readonly isNotFound = computed(() => (this.detail.error() ?? '').includes('404'));

  protected readonly subIssueColumns: readonly DataColumn[] = [
    { key: 'issue', header: 'Sub-issue' },
    { key: 'stage', header: 'Stage', width: '190px' },
    { key: 'next', header: 'Next', width: '220px' },
    { key: 'pulls', header: 'Pull requests', hideBelow: 'sm' },
  ];

  protected readonly pullColumns: readonly DataColumn[] = [
    { key: 'pull', header: 'Pull request' },
    { key: 'branches', header: 'Branches', hideBelow: 'sm' },
    { key: 'state', header: 'State', width: '120px' },
    { key: 'actions', header: 'Actions', width: '200px' },
  ];

  protected readonly jobColumns: readonly DataColumn[] = [
    { key: 'type', header: 'Job', width: '170px' },
    { key: 'status', header: 'Status', width: '120px' },
    { key: 'result', header: 'Result' },
    { key: 'duration', header: 'Duration', align: 'end', width: '110px', hideBelow: 'sm' },
    // No cell template: the table shows the number, or "-" for an old row or a failed job (null).
    { key: 'copilotCalls', header: 'Calls', align: 'end', width: '70px', hideBelow: 'sm' },
    { key: 'startedAt', header: 'Started', align: 'end', width: '170px', hideBelow: 'md' },
    { key: 'log', header: 'Log', width: '90px' },
  ];

  protected readonly markTone = issueMarkTone;
  protected readonly markIcon = issueMarkIcon;
  protected readonly statusTone = jobStatusTone;
  protected readonly link = issueLink;
  protected readonly issueTarget = (row: IssueRowResponse): ActionTarget => ({
    kind: 'issue',
    repo: row.repo,
    number: row.number,
  });
  protected readonly pullTarget = (row: PullResponse): ActionTarget => ({
    kind: 'pull',
    repo: row.repo,
    number: row.number,
  });

  protected readonly trackBySubIssue = (_index: number, row: IssueRowResponse) => issueKey(row);
  protected readonly trackByPull = (_index: number, row: PullResponse) => issueKey(row);
  protected readonly issueKey = issueKey;
  protected readonly trackByJob = (_index: number, row: JobResponse) => row.id;
}
