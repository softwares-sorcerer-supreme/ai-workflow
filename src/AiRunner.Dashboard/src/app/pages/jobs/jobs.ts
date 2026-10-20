import { Component, inject, signal } from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { Router } from '@angular/router';
import { Api } from '../../api/api';
import { getJobs } from '../../api/fn/jobs/get-jobs';
import { JobResponse } from '../../api/models/job-response';
import { DataCell, DataColumn, DataTable } from '../../shared/data-table/data-table';
import { PageHeader } from '../../shared/page-header/page-header';
import { pollResource } from '../../shared/poll-resource';
import { StatusChip } from '../../shared/status-chip/status-chip';
import { DurationPipe } from '../../shared/time/duration.pipe';
import { AppTime } from '../../shared/time/time';
import { jobStatusTone } from '../../shared/tone';
import { JOB_STATUS_OPTIONS, JOB_TYPE_OPTIONS } from './job-filters';

/** The job history, newest first, one page at a time, filtered by status and type. A row opens the job's log. */
@Component({
  selector: 'app-jobs',
  imports: [
    AppTime,
    DataCell,
    DataTable,
    DurationPipe,
    MatFormFieldModule,
    MatPaginatorModule,
    MatSelectModule,
    PageHeader,
    StatusChip,
  ],
  templateUrl: './jobs.html',
  styleUrl: './jobs.scss',
})
export class Jobs {
  private readonly api = inject(Api);
  private readonly router = inject(Router);

  protected readonly statusOptions = JOB_STATUS_OPTIONS;
  protected readonly typeOptions = JOB_TYPE_OPTIONS;

  /** Empty means "all". */
  protected readonly status = signal('');
  protected readonly type = signal('');
  /** 1-based, like the API. */
  protected readonly page = signal(1);

  protected readonly jobs = pollResource(() =>
    this.api.invoke(getJobs, {
      status: this.status() || undefined,
      type: this.type() || undefined,
      page: this.page(),
    }),
  );

  protected readonly columns: readonly DataColumn[] = [
    { key: 'status', header: 'Status', width: '120px' },
    { key: 'type', header: 'Type', width: '170px' },
    { key: 'issue', header: 'Issue' },
    { key: 'startedAt', header: 'Started', width: '170px', hideBelow: 'sm' },
    { key: 'duration', header: 'Duration', align: 'end', width: '110px', hideBelow: 'sm' },
    // No cell template: the table shows the number, or "-" for an old row or a failed job (null).
    { key: 'copilotCalls', header: 'Calls', align: 'end', width: '70px', hideBelow: 'sm' },
    { key: 'result', header: 'Result', hideBelow: 'md' },
  ];

  protected readonly statusTone = jobStatusTone;
  protected readonly trackById = (_index: number, row: JobResponse) => row.id;

  protected setStatus(code: string): void {
    this.status.set(code);
    this.showPage(1);
  }

  protected setType(code: string): void {
    this.type.set(code);
    this.showPage(1);
  }

  protected onPage(event: PageEvent): void {
    this.showPage(event.pageIndex + 1);
  }

  protected open(job: JobResponse): void {
    void this.router.navigate(['/jobs', job.id]);
  }

  private showPage(page: number): void {
    this.page.set(page);
    this.jobs.reload();
  }
}
