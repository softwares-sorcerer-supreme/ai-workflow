import { Component, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { Router, RouterLink } from '@angular/router';
import { Api } from '../../api/api';
import { getOverview } from '../../api/fn/overview/get-overview';
import { JobResponse } from '../../api/models/job-response';
import { PollErrorResponse } from '../../api/models/poll-error-response';
import { HealthStore } from '../../layout/health-store';
import { DataCell, DataColumn, DataTable } from '../../shared/data-table/data-table';
import { InlineNote } from '../../shared/inline-note/inline-note';
import { PageHeader } from '../../shared/page-header/page-header';
import { pollResource } from '../../shared/poll-resource';
import { StatusChip } from '../../shared/status-chip/status-chip';
import { DurationPipe } from '../../shared/time/duration.pipe';
import { AppTime } from '../../shared/time/time';
import { healthTone, jobStatusTone } from '../../shared/tone';
import { ViewState } from '../../shared/view-state/view-state';

/**
 * What the runner is doing now: the running job, the queue, the runner's facts and the poll errors per project.
 * The "waiting on you" list joins this screen in a later version.
 */
@Component({
  selector: 'app-overview',
  imports: [
    AppTime,
    DataCell,
    DataTable,
    DurationPipe,
    InlineNote,
    MatButtonModule,
    MatIconModule,
    PageHeader,
    RouterLink,
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

  protected readonly overview = pollResource(() => this.api.invoke(getOverview));

  protected readonly health = this.resource.value;
  protected readonly error = this.resource.error;
  protected readonly isLoading = this.resource.isLoading;

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

  protected readonly statusTone = jobStatusTone;
  protected readonly trackById = (_index: number, row: JobResponse) => row.id;

  protected readonly trackByProject = (_index: number, row: PollErrorResponse) => row.project;

  protected openJob(job: JobResponse): void {
    void this.router.navigate(['/jobs', job.id]);
  }

  protected reload(): void {
    this.resource.reload();
  }
}
