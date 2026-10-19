import { Component, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { Tone } from '../tone';

/**
 * One tone colored line inside a panel: "Showing older data", "Showing the first 2 MB of this file". The text is
 * projected content. Use it where a panel keeps its data but must say something about it; the data table has its
 * own banner.
 */
@Component({
  selector: 'app-inline-note',
  imports: [MatIconModule],
  template: `
    <mat-icon aria-hidden="true">{{ icon() }}</mat-icon>
    <span class="text"><ng-content /></span>
  `,
  styleUrl: './inline-note.scss',
  host: {
    '[class]': '"tone-" + tone()',
    role: 'status',
  },
})
export class InlineNote {
  readonly tone = input<Tone>('warning');
  /** Material Symbols name. */
  readonly icon = input('warning');
}
