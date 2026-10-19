import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Api } from '../../api/api';
import { getJob } from '../../api/fn/jobs/get-job';
import { getJobFile } from '../../api/fn/jobs/get-job-file';
import { getJobFiles } from '../../api/fn/jobs/get-job-files';
import { JobResponse } from '../../api/models/job-response';
import { LogFileTextResponse } from '../../api/models/log-file-text-response';
import { FakeApi, fixedClock, sampleJob, settle } from '../../shared/testing';
import { JobLog } from './job-log';

const FILES = [
  { name: 'context.md', format: 'markdown', sizeBytes: 2048 },
  { name: 'round-1-copilot-out.txt', format: 'text', sizeBytes: 300 },
];

function textFile(overrides: Partial<LogFileTextResponse>): LogFileTextResponse {
  return {
    name: 'context.md',
    format: 'markdown',
    sizeBytes: 10,
    text: '',
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
});
