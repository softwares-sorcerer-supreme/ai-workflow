import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { Api } from '../../api/api';
import { getJob } from '../../api/fn/jobs/get-job';
import { getJobFile } from '../../api/fn/jobs/get-job-file';
import { getJobFiles } from '../../api/fn/jobs/get-job-files';
import { LogFileTextResponse } from '../../api/models/log-file-text-response';
import { InlineNote } from '../../shared/inline-note/inline-note';
import { Markdown } from '../../shared/markdown/markdown';
import { PageHeader } from '../../shared/page-header/page-header';
import { pollResource } from '../../shared/poll-resource';
import { StatusChip } from '../../shared/status-chip/status-chip';
import { DurationPipe } from '../../shared/time/duration.pipe';
import { AppTime } from '../../shared/time/time';
import { jobStatusTone } from '../../shared/tone';
import { ViewState } from '../../shared/view-state/view-state';

/**
 * One job: its facts, its log files and the chosen file. The file name goes to the generated client as it came
 * from the file list; the runner checks it, the UI never builds a path or URL from it.
 */
@Component({
  selector: 'app-job-log',
  imports: [
    AppTime,
    DurationPipe,
    InlineNote,
    Markdown,
    MatButtonModule,
    MatIconModule,
    MatListModule,
    PageHeader,
    RouterLink,
    StatusChip,
    ViewState,
  ],
  templateUrl: './job-log.html',
  styleUrl: './job-log.scss',
})
export class JobLog {
  private readonly api = inject(Api);

  // Read from the route synchronously (paramMap replays its value), so the first poll already has the id.
  protected readonly id = toSignal(
    inject(ActivatedRoute).paramMap.pipe(map((params) => Number(params.get('id')))),
    {
      requireSync: true,
    },
  );

  protected readonly job = pollResource(() => this.api.invoke(getJob, { id: this.id() }));
  protected readonly files = pollResource(() => this.api.invoke(getJobFiles, { id: this.id() }));

  /** The file the user picked; until then the first file of the list. */
  private readonly chosenName = signal<string | undefined>(undefined);

  protected readonly selectedName = computed(
    () => this.chosenName() ?? this.files.value()?.files[0]?.name,
  );

  protected readonly file = pollResource<LogFileTextResponse | undefined>(() => {
    const name = this.selectedName();
    if (!name) {
      return Promise.resolve(undefined);
    }

    return this.api.invoke(getJobFile, { id: this.id(), name });
  });

  /** The loaded file, only when it is the selected one (a click shows loading, never the previous file). */
  protected readonly shownFile = computed(() => {
    const file = this.file.value();

    return file && file.name === this.selectedName() ? file : undefined;
  });

  // Separate signals so a poll that brings the same text does not render the Markdown again.
  protected readonly fileText = computed(() => this.shownFile()?.text ?? '');
  protected readonly isMarkdown = computed(() => this.shownFile()?.format === 'markdown');

  protected readonly statusTone = jobStatusTone;

  constructor() {
    // A new route id: reload everything. A new selection: load that file now instead of at the next poll.
    let loadedId = this.id();

    effect(() => {
      const id = this.id();
      if (id === loadedId) {
        return;
      }

      loadedId = id;
      untracked(() => {
        this.chosenName.set(undefined);
        this.job.reload();
        this.files.reload();
      });
    });

    effect(() => {
      const name = this.selectedName();
      if (name) {
        untracked(() => this.file.reload());
      }
    });
  }

  protected select(name: string): void {
    this.chosenName.set(name);
  }

  /** File size for the list: "512 B", "12.4 KB", "2.1 MB". */
  protected sizeOf(bytes: number): string {
    if (bytes < 1024) {
      return `${bytes} B`;
    }

    const kilobytes = bytes / 1024;
    if (kilobytes < 1024) {
      return `${kilobytes.toFixed(1)} KB`;
    }

    return `${(kilobytes / 1024).toFixed(1)} MB`;
  }
}
