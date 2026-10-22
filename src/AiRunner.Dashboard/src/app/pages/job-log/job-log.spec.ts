import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Api } from '../../api/api';
import { getJob } from '../../api/fn/jobs/get-job';
import { getJobFile } from '../../api/fn/jobs/get-job-file';
import { getJobFiles } from '../../api/fn/jobs/get-job-files';
import { JobResponse } from '../../api/models/job-response';
import { LogFileTextResponse } from '../../api/models/log-file-text-response';
import { FakeApi, fixedClock, sampleJob, sampleStep, settle } from '../../shared/testing';
import { JobLog } from './job-log';

const FILES = [
  { name: 'context.md', format: 'markdown', sizeBytes: 2048, isLive: false },
  { name: 'round-1-copilot-out.txt', format: 'text', sizeBytes: 300, isLive: false },
];

function textFile(overrides: Partial<LogFileTextResponse>): LogFileTextResponse {
  return {
    name: 'context.md',
    format: 'markdown',
    sizeBytes: 10,
    text: '',
    offset: 0,
    nextOffset: 10,
    isTruncated: false,
    truncatedNote: null,
    ...overrides,
  };
}

describe('JobLog', () => {
  async function render(options: {
    job?: JobResponse;
    files?: typeof FILES;
    file?: (name: string) => LogFileTextResponse;
  }) {
    const api = new FakeApi()
      .answer(getJob, () => options.job ?? sampleJob())
      .answer(getJobFiles, () => ({ jobId: 7, files: options.files ?? FILES }))
      .answer(getJobFile, (params) =>
        (options.file ?? ((name: string) => textFile({ name, text: '# Context' })))(params.name),
      );

    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'jobs/:id', component: JobLog }]),
        fixedClock,
        { provide: Api, useValue: api },
      ],
    });

    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/jobs/7', JobLog);
    await settle(harness.fixture);

    return { api, harness, element: harness.routeNativeElement as HTMLElement };
  }

  it('shows the job facts and loads the first file of the list', async () => {
    const { api, element } = await render({});

    expect(element.querySelector('h1')?.textContent).toContain('Job 7');
    expect(element.textContent).toContain('Implement issue');
    expect(element.textContent).toContain('team/shop#42');
    expect(api.callsOf(getJob)).toContainEqual({ id: 7 });
    expect(api.callsOf(getJobFile)).toEqual([{ id: 7, name: 'context.md' }]);
    expect(element.querySelector('app-markdown h1')?.textContent).toBe('Context');
  });

  it('shows the model of the job with its reason', async () => {
    const { element } = await render({
      job: sampleJob({ model: 'claude-sonnet-5.5', modelReason: 'fix rounds' }),
    });

    expect(element.querySelector('[data-testid="job-model"]')?.textContent?.trim()).toBe(
      'claude-sonnet-5.5 - fix rounds',
    );
  });

  it('loads exactly the file that was selected and shows text in a monospace block', async () => {
    const { api, harness, element } = await render({
      file: (name) =>
        name === 'round-1-copilot-out.txt'
          ? textFile({ name, format: 'text', text: '# not a heading\nline 2' })
          : textFile({ name, text: '# Context' }),
    });

    const buttons = [...element.querySelectorAll<HTMLButtonElement>('.files button')];
    buttons[1].click();
    await settle(harness.fixture);

    expect(api.callsOf(getJobFile).at(-1)).toEqual({ id: 7, name: 'round-1-copilot-out.txt' });
    const text = element.querySelector('pre.app-mono');
    expect(text?.textContent).toBe('# not a heading\nline 2');
    expect(element.querySelector('app-markdown')).toBeNull();
    expect(buttons[1].getAttribute('aria-current')).toBe('true');
  });

  it('shows the truncated note of a cut file', async () => {
    const { element } = await render({
      file: (name) =>
        textFile({
          name,
          text: 'start',
          isTruncated: true,
          truncatedNote: 'Showing the first 2 MB of this file.',
        }),
    });

    expect(element.querySelector('app-inline-note')?.textContent).toContain(
      'Showing the first 2 MB of this file.',
    );
  });

  it('never turns HTML in a Markdown log into elements', async () => {
    const { element } = await render({
      file: (name) =>
        textFile({ name, text: 'Hi <script>alert(1)</script>\n\n<img src=x onerror="alert(2)">' }),
    });

    const viewer = element.querySelector('.viewer') as HTMLElement;
    expect(viewer.querySelector('script')).toBeNull();
    expect(viewer.querySelector('img')).toBeNull();
    expect(viewer.textContent).toContain('<script>alert(1)</script>');
  });

  it('shows an empty state for a job without a log', async () => {
    const { api, element } = await render({ job: sampleJob({ hasLog: false }), files: [] });

    expect(element.textContent).toContain('No log for this job');
    expect(api.callsOf(getJobFile)).toEqual([]);
  });

  it('shows the steps of the job', async () => {
    const { element } = await render({
      job: sampleJob({
        steps: [
          sampleStep({ seq: 1 }),
          sampleStep({
            seq: 2,
            code: 'check',
            label: 'build',
            state: 'failed',
            stateLabel: 'Failed',
            detail: 'build failed',
          }),
        ],
      }),
    });

    const steps = [...element.querySelectorAll('app-job-steps li.step')];
    expect(steps.length).toBe(2);
    expect(steps[1].textContent).toContain('build failed');
  });

  it('says no steps were recorded for an old job', async () => {
    const { element } = await render({ job: sampleJob({ steps: null }) });

    expect(element.querySelector('app-job-steps')?.textContent).toContain('No steps recorded');
  });
});

describe('JobLog live tail', () => {
  const LIVE = 'round-1-copilot-live.txt';
  const CONTENT = 'line 1\nline 2\nthe end\n';

  /** What the fake runner has: whether the job runs and how many bytes of the live file exist so far. */
  let isRunning: boolean;
  let available: number;
  /** The file list's `isLive` for the file: the only thing that makes the page tail it. */
  let isLive: boolean;

  beforeEach(() => {
    isLive = true;
  });

  async function render() {
    const api = new FakeApi()
      .answer(getJob, () => sampleJob({ isRunning, finishedAt: null }))
      .answer(getJobFiles, () => ({
        jobId: 7,
        files: [{ name: LIVE, format: 'text', sizeBytes: available, isLive }],
      }))
      .answer(getJobFile, (params) => {
        const offset = params.offset ?? 0;

        return textFile({
          name: params.name,
          format: 'text',
          sizeBytes: available,
          text: CONTENT.slice(offset, available),
          offset,
          nextOffset: available,
        });
      });

    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'jobs/:id', component: JobLog }]),
        fixedClock,
        { provide: Api, useValue: api },
      ],
    });

    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/jobs/7', JobLog);
    await settle(harness.fixture);

    /** One poll of every resource of the page, as the 3 s timer would do (the tab "comes back"). */
    const poll = async () => {
      TestBed.inject(DOCUMENT).dispatchEvent(new Event('visibilitychange'));
      await settle(harness.fixture);
    };

    const text = () => (harness.routeNativeElement as HTMLElement).querySelector('pre.log-text')?.textContent;

    return { api, poll, text };
  }

  it('appends from the previous offset while the job runs, then reads once more after it ended', async () => {
    isRunning = true;
    available = 7;
    const { api, poll, text } = await render();

    expect(api.callsOf(getJobFile)).toEqual([{ id: 7, name: LIVE }]);
    expect(text()).toBe('line 1\n');

    available = 14;
    await poll();

    expect(api.callsOf(getJobFile).at(-1)).toEqual({ id: 7, name: LIVE, offset: 7 });
    expect(text()).toBe('line 1\nline 2\n');

    // The job ends. This poll's file read starts before the new job state arrived: still a running read.
    isRunning = false;
    await poll();

    expect(api.callsOf(getJobFile).at(-1)).toEqual({ id: 7, name: LIVE, offset: 14 });
    expect(text()).toBe('line 1\nline 2\n');

    // The last bytes came after that read; the one read after the job ended brings them.
    available = CONTENT.length;
    await poll();

    expect(api.callsOf(getJobFile).at(-1)).toEqual({ id: 7, name: LIVE, offset: 14 });
    expect(text()).toBe(CONTENT);

    const reads = api.callsOf(getJobFile).length;
    await poll();
    await poll();

    expect(api.callsOf(getJobFile).length).toBe(reads);
    expect(text()).toBe(CONTENT);
  });

  it('reads the live file of a finished job once', async () => {
    isRunning = false;
    available = CONTENT.length;
    const { api, poll, text } = await render();

    expect(text()).toBe(CONTENT);
    const reads = api.callsOf(getJobFile).length;

    await poll();

    expect(api.callsOf(getJobFile).length).toBe(reads);
    expect(text()).toBe(CONTENT);
  });

  it('reads a file the list does not mark live as a whole file each poll, whatever its name', async () => {
    isLive = false;
    isRunning = true;
    available = 7;
    const { api, poll, text } = await render();

    available = 14;
    await poll();

    expect(api.callsOf(getJobFile)).toEqual([
      { id: 7, name: LIVE },
      { id: 7, name: LIVE },
    ]);
    expect(text()).toBe(CONTENT.slice(0, 14));
  });
});
