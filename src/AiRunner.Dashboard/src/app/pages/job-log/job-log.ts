import {
  Component,
  ElementRef,
  afterRenderEffect,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
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
import { JobSteps } from '../../shared/job-steps/job-steps';
import { Markdown } from '../../shared/markdown/markdown';
import { PageHeader } from '../../shared/page-header/page-header';
import { pollResource } from '../../shared/poll-resource';
import { StatusChip } from '../../shared/status-chip/status-chip';
import { DurationPipe } from '../../shared/time/duration.pipe';
import { AppTime } from '../../shared/time/time';
import { jobStatusTone } from '../../shared/tone';
import { ViewState } from '../../shared/view-state/view-state';

/** How far a live file was read: the text so far and the byte offset of the next read. */
interface LogTail {
  readonly key: string;
  readonly file: LogFileTextResponse;
  readonly nextOffset: number;
  /** The last read started after the job had ended: it holds the end of the log, so no read follows. */
  readonly isFinished: boolean;
}

/** How close to the end (px) still counts as "at the end": the view then follows new text. */
const FOLLOW_SLACK_PX = 24;

/**
 * One job: its facts and steps, its log files and the chosen file. A live file of a running job is tailed: each
 * poll reads from the previous `nextOffset` and appends; one more read after the job ended brings the end. The file
 * name goes to the generated client as it came from the file list; the runner checks it, the UI never builds a path
 * or URL from it.
 */
@Component({
  selector: 'app-job-log',
  imports: [
    AppTime,
    DurationPipe,
    InlineNote,
    JobSteps,
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

  /**
   * The selected file is one the runner appends to while Copilot runs (`isLive` from the file list): it is tailed
   * while the job runs, and its text box scrolls on its own and follows the end.
   */
  protected readonly isLiveFile = computed(() => {
    const name = this.selectedName();
    const entry = this.files.value()?.files.find((candidate) => candidate.name === name);

    return entry?.isLive === true;
  });

  protected readonly file = pollResource<LogFileTextResponse | undefined>(() => {
    const name = this.selectedName();
    if (!name) {
      return Promise.resolve(undefined);
    }

    if (this.isLiveFile()) {
      return this.readTail(this.id(), name);
    }

    return this.api.invoke(getJobFile, { id: this.id(), name });
  });

  private readonly logText = viewChild<ElementRef<HTMLElement>>('logText');

  /** The live file read so far; reset when the job or the file changes. */
  private tail: LogTail | undefined;

  /** False once the user scrolled up in a live log, true again at its end. */
  private isFollowing = true;

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

    // New text in a live log: keep its end in view, unless the user scrolled up to read.
    afterRenderEffect(() => {
      this.fileText();
      const element = this.logText()?.nativeElement;

      if (element && this.isLiveFile() && this.isFollowing) {
        element.scrollTop = element.scrollHeight;
      }
    });
  }

  protected select(name: string): void {
    this.chosenName.set(name);
    this.isFollowing = true;
  }

  protected onLogScroll(): void {
    const element = this.logText()?.nativeElement;
    if (!element) {
      return;
    }

    const distanceToEnd = element.scrollHeight - element.scrollTop - element.clientHeight;
    this.isFollowing = distanceToEnd <= FOLLOW_SLACK_PX;
  }

  /**
   * One poll of a live file: the first read from the start, each later one from the previous `nextOffset`, its text
   * appended. The job's state is taken before the call, so a read that starts after the job ended has the end of the
   * log; after it the file is not read again (the poll keeps the same answer).
   */
  private async readTail(id: number, name: string): Promise<LogFileTextResponse> {
    const key = `${id}/${name}`;
    const previous = this.tail?.key === key ? this.tail : undefined;

    if (previous?.isFinished) {
      return previous.file;
    }

    const job = this.job.value();
    const hasJobEnded = job !== undefined && job.id === id && !job.isRunning;

    let answer: LogFileTextResponse;
    if (previous) {
      answer = await this.api.invoke(getJobFile, { id, name, offset: previous.nextOffset });
    } else {
      answer = await this.api.invoke(getJobFile, { id, name });
    }

    const file: LogFileTextResponse = { ...answer, text: (previous?.file.text ?? '') + answer.text };
    this.tail = {
      key,
      file,
      nextOffset: answer.nextOffset,
      isFinished: hasJobEnded,
    };

    return file;
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
