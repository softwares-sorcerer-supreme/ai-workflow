import { DecimalPipe } from '@angular/common';
import { Component, input } from '@angular/core';
import { Tone } from '../tone';

/** `chip`: a small pill in a row of quick filters. `segment`: a wide tile with a large count (a pipeline step). */
export type CountToggleLook = 'chip' | 'segment';

/**
 * A toned toggle button with a label and the count the API sent: the focus chips and the pipeline segments of the
 * issues screen. It is the button itself (`<button appCountToggle>`), so a click handler, `data-*` attributes and
 * the focus ring stay on the host. Without a `tone` it shows no dot (the "All" chip); without a `count`, no number.
 */
@Component({
  selector: 'button[appCountToggle]',
  imports: [DecimalPipe],
  template: `
    <span class="label">
      @if (tone()) {
        <span class="dot" aria-hidden="true"></span>
      }
      {{ label() }}
    </span>
    @if (count() !== undefined) {
      &ngsp;<span class="count">{{ count() | number }}</span>
    }
  `,
  styleUrl: './count-toggle.scss',
  host: {
    type: 'button',
    '[class]': '"tone-" + (tone() ?? "neutral") + " " + look()',
    '[class.selected]': 'pressed()',
    '[attr.aria-pressed]': 'pressed()',
  },
})
export class CountToggle {
  readonly label = input.required<string>();
  readonly count = input<number>();
  readonly tone = input<Tone>();
  readonly pressed = input(false);
  readonly look = input<CountToggleLook>('chip');
}
