import { Component, input } from '@angular/core';
import { JobStepResponse } from '../../api/models/job-step-response';
import { StatusChip } from '../status-chip/status-chip';
import { DurationPipe } from '../time/duration.pipe';
import { stepStateIcon, stepStateTone } from '../tone';

/**
 * The steps of one job as the runner recorded them (claim, prepare, Copilot round N, build...), in the API's order:
 * state chip, label, duration and the step's detail as a second line. The state only picks a color and an icon.
 */
@Component({
  selector: 'app-job-steps',
  imports: [DurationPipe, StatusChip],
  templateUrl: './job-steps.html',
  styleUrl: './job-steps.scss',
})
export class JobSteps {
  /** The job's steps; null or empty for a job from before steps were recorded. */
  readonly steps = input<readonly JobStepResponse[] | null | undefined>();

  protected readonly stateTone = stepStateTone;
  protected readonly stateIcon = stepStateIcon;
}
