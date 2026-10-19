import { Component, computed, input } from '@angular/core';
import { StatusChip } from '../status-chip/status-chip';
import { Tone, stageTone } from '../tone';

/** The workflow stage of an issue: label text from the API, color from the stage code (`STAGE_TONES`). */
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

  protected readonly tone = computed<Tone>(() => stageTone(this.code()));
}
