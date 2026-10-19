import { Component, computed, inject } from '@angular/core';
import { PollErrorResponse } from '../../api/models/poll-error-response';
import { HealthStore } from '../../layout/health-store';
import { HEALTH_TONES } from '../../layout/health-strip/health-strip';
import { DataCell, DataColumn, DataTable } from '../../shared/data-table/data-table';
import { PageHeader } from '../../shared/page-header/page-header';
import { StatusChip } from '../../shared/status-chip/status-chip';
import { DurationPipe } from '../../shared/time/duration.pipe';
import { AppTime } from '../../shared/time/time';
import { ViewState } from '../../shared/view-state/view-state';

/**
 * What the runner is doing now. For now: the runner's facts and the poll errors per project. The running job, the
 * queue and the "waiting on you" list join this screen in later versions.
 */
@Component({
  selector: 'app-overview',
  imports: [AppTime, DataCell, DataTable, DurationPipe, PageHeader, StatusChip, ViewState],
  templateUrl: './overview.html',
  styleUrl: './overview.scss',
})
export class Overview {
  private readonly resource = inject(HealthStore).health;

  protected readonly health = this.resource.value;
  protected readonly error = this.resource.error;
  protected readonly isLoading = this.resource.isLoading;

  protected readonly healthTone = computed(() => HEALTH_TONES[this.health()?.state ?? ''] ?? 'neutral');

  protected readonly errorColumns: readonly DataColumn[] = [
    { key: 'project', header: 'Project', width: '180px' },
    { key: 'message', header: 'Error' },
    { key: 'at', header: 'When', align: 'end', width: '140px', hideBelow: 'sm' },
  ];

  protected readonly trackByProject = (_index: number, row: PollErrorResponse) => row.project;

  protected reload(): void {
    this.resource.reload();
  }
}
