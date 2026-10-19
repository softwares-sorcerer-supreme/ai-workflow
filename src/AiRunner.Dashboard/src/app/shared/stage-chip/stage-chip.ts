import { Component, computed, input } from '@angular/core';
import { StatusChip } from '../status-chip/status-chip';
import { Tone } from '../tone';

/**
 * Color per stage code. The API computes the stage (C# Core/IssueStage) and sends its code and label; this map only
 * picks a color. A code that is not here shows as neutral, so a new stage never breaks the screen.
 */
export const STAGE_TONES: Readonly<Record<string, Tone>> = {
  new: 'neutral',
  'questions-open': 'warning',
  'ready-simple': 'info',
  'ready-complex': 'info',
  implementing: 'progress',
  'broken-down': 'progress',
  'pr-open': 'info',
  merged: 'success',
  done: 'success',
  'closed-no-merge': 'neutral',
};

/** The workflow stage of an issue: label text from the API, color from the stage code. */
@Component({
  selector: 'app-stage-chip',
  imports: [StatusChip],
  template: `<app-status-chip [label]="label()" [tone]="tone()" />`,
  host: { '[attr.data-stage]': 'code()' },
})
export class StageChip {
  /** Stable stage code from the API, e.g. `pr-open`. */
  readonly code = input.required<string>();
  /** Display text from the API, e.g. `PR open` or `Broken down (2/5)`. */
  readonly label = input.required<string>();

  protected readonly tone = computed<Tone>(() => STAGE_TONES[this.code()] ?? 'neutral');
}
