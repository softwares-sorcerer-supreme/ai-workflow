import { TestBed } from '@angular/core/testing';
import { JobStepResponse } from '../../api/models/job-step-response';
import { sampleStep } from '../testing';
import { JobSteps } from './job-steps';

describe('JobSteps', () => {
  async function render(steps: JobStepResponse[] | null) {
    const fixture = TestBed.createComponent(JobSteps);
    fixture.componentRef.setInput('steps', steps);
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  it('shows each step with its state label, tone, icon, label and duration', async () => {
    const element = await render([
      sampleStep({ seq: 1 }),
      sampleStep({
        seq: 2,
        code: 'copilot',
        label: 'Copilot round 1',
        state: 'running',
        stateLabel: 'Running',
        endedAt: null,
        durationSeconds: null,
      }),
      sampleStep({ seq: 3, code: 'check', label: 'build', state: 'failed', stateLabel: 'Failed' }),
      sampleStep({ seq: 4, code: 'push', label: 'Push', state: 'interrupted', stateLabel: 'Interrupted' }),
    ]);

    const rows = [...element.querySelectorAll('li.step')];
    expect(rows.length).toBe(4);
    expect(rows[0].textContent).toContain('Done');
    expect(rows[0].textContent).toContain('Prepare workspace');
    expect(rows[0].textContent).toContain('12 s');

    const chips = rows.map((row) => row.querySelector('app-status-chip') as HTMLElement);
    expect(chips.map((chip) => [...chip.classList].find((name) => name.startsWith('tone-')))).toEqual([
      'tone-success',
      'tone-progress',
      'tone-danger',
      'tone-warning',
    ]);
    expect(chips.map((chip) => chip.querySelector('mat-icon')?.textContent)).toEqual([
      'check_circle',
      'progress_activity',
      'error',
      'cancel',
    ]);

    expect(rows[1].classList).toContain('tone-progress');
    expect(rows[1].textContent).toContain('Running');
    expect(rows[1].textContent).toContain('Copilot round 1');
    expect(rows[3].textContent).toContain('Interrupted');
  });

  it('shows the detail of a failed step as a second line', async () => {
    const element = await render([
      sampleStep({ state: 'failed', stateLabel: 'Failed', detail: 'build failed: 3 errors' }),
    ]);

    expect(element.querySelector('.detail')?.textContent).toBe('build failed: 3 errors');
  });

  it('shows no detail line when the step has none', async () => {
    const element = await render([sampleStep()]);

    expect(element.querySelector('.detail')).toBeNull();
  });

  it('says no steps were recorded for an empty or missing list', async () => {
    expect((await render([])).textContent).toContain('No steps recorded');
    expect((await render(null)).textContent).toContain('No steps recorded');
  });
});
