import { Component, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Router, RouterLink } from '@angular/router';
import { Api } from '../../api/api';
import { cancelJob } from '../../api/fn/jobs/cancel-job';
import { getOverview } from '../../api/fn/overview/get-overview';
import { pauseRunner } from '../../api/fn/runner/pause-runner';
import { pollNow } from '../../api/fn/runner/poll-now';
import { resumeRunner } from '../../api/fn/runner/resume-runner';
import { JobResponse } from '../../api/models/job-response';
import { RunningJobResponse } from '../../api/models/running-job-response';
import { WaitingItemResponse } from '../../api/models/waiting-item-response';
import { PollErrorResponse } from '../../api/models/poll-error-response';
import { HealthStore } from '../../layout/health-store';
import { confirmWrite } from '../../shared/confirm-dialog/confirm-dialog';
import { DataCell, DataColumn, DataTable } from '../../shared/data-table/data-table';
import { InlineNote } from '../../shared/inline-note/inline-note';
import { issueLink } from '../../shared/issue-link';
import { JobSteps } from '../../shared/job-steps/job-steps';
import { PageHeader } from '../../shared/page-header/page-header';
import { pollResource } from '../../shared/poll-resource';
import { StageChip } from '../../shared/stage-chip/stage-chip';
import { StatusChip } from '../../shared/status-chip/status-chip';
import { DurationPipe } from '../../shared/time/duration.pipe';
import { AppTime } from '../../shared/time/time';
import { healthTone, jobStatusTone } from '../../shared/tone';
import { ViewState } from '../../shared/view-state/view-state';
import { writeAction } from '../../shared/write-action';

/** What the cancel confirmation says: what happens to the job and how to run it again. */
export const CANCEL_JOB_MESSAGE =
  'The job stops now and does not start again by itself. The runner posts a failure comment on its issue or PR ' +
  'that says how to run it again (on an issue it also adds ai:failed; removing that label retries it).';

/**
 * What the runner is doing now: the running job, the queue, the runner's facts and the poll errors per project.
 * Below them the "waiting on you" list: issues and PRs that need a person, longest waiting first. The runner card
 * holds Poll now and Pause / Resume, the running job card holds Cancel (after a confirmation).
 */
@Component({
  selector: 'app-overview',
  imports: [
    AppTime,
    DataCell,
    DataTable,
    DurationPipe,
    InlineNote,
    JobSteps,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    PageHeader,
    RouterLink,
    StageChip,
    StatusChip,
    ViewState,
  ],
  templateUrl: './overview.html',
  styleUrl: './overview.scss',
})
export class Overview {
  private readonly resource = inject(HealthStore).health;
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);

  protected readonly overview = pollResource(() => this.api.invoke(getOverview));

  protected readonly health = this.resource.value;
  protected readonly error = this.resource.error;
  protected readonly isLoading = this.resource.isLoading;

  protected readonly pollNow = writeAction({
    failure: 'The runner did not start a poll.',
    after: () => this.resource.reload(),
  });

  protected readonly pauseOrResume = writeAction({
    failure: 'The runner did not change its pause state.',
    after: () => this.resource.reload(),
  });

  protected readonly cancel = writeAction({
    failure: 'The runner did not cancel the job.',
    after: () => {
      this.overview.reload();
      this.resource.reload();
    },
  });

  protected readonly healthTone = computed(() => healthTone(this.health()?.state));

  protected readonly errorColumns: readonly DataColumn[] = [
    { key: 'project', header: 'Project', width: '180px' },
    { key: 'message', header: 'Error' },
    { key: 'at', header: 'When', align: 'end', width: '140px', hideBelow: 'sm' },
  ];

  protected readonly queueColumns: readonly DataColumn[] = [
    { key: 'status', header: 'Status', width: '120px' },
    { key: 'type', header: 'Type', width: '170px' },
    { key: 'issue', header: 'Issue' },
    { key: 'createdAt', header: 'Queued', align: 'end', width: '170px', hideBelow: 'sm' },
  ];

  protected readonly waitingColumns: readonly DataColumn[] = [
    { key: 'stage', header: 'Stage', width: '190px' },
    { key: 'action', header: 'Action', width: '280px' },
    { key: 'issue', header: 'Issue' },
  ];

  protected readonly statusTone = jobStatusTone;
  protected readonly trackById = (_index: number, row: JobResponse) => row.id;

  protected readonly trackByWaiting = (_index: number, row: WaitingItemResponse) => `${row.repo}#${row.number}`;
  protected readonly link = (row: WaitingItemResponse) => issueLink(row.repo, row.number);

  protected readonly trackByProject = (_index: number, row: PollErrorResponse) => row.project;

  protected openJob(job: JobResponse): void {
    void this.router.navigate(['/jobs', job.id]);
  }

  protected reload(): void {
    this.resource.reload();
  }

  protected startPoll(): void {
    void this.pollNow.run(() => this.api.invoke(pollNow));
  }

  /** Pause or resume, from the runner's own `isPaused`: the button never keeps a state of its own. */
  protected togglePause(isPaused: boolean): void {
    if (isPaused) {
      void this.pauseOrResume.run(() => this.api.invoke(resumeRunner));
    } else {
      void this.pauseOrResume.run(() => this.api.invoke(pauseRunner));
    }
  }

  protected async cancelJob(job: RunningJobResponse): Promise<void> {
    const isConfirmed = await confirmWrite(this.dialog, {
      title: 'Cancel the running job?',
      message: CANCEL_JOB_MESSAGE,
      confirmLabel: 'Cancel job',
      dismissLabel: 'Keep running',
    });

    if (!isConfirmed) {
      return;
    }

    await this.cancel.run(() => this.api.invoke(cancelJob, { id: job.id }));
  }
}
