import { Component, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { Tone } from '../tone';

/** A small colored label for a state the API sent (flags, outcomes, badges). Not clickable. */
@Component({
  selector: 'app-status-chip',
  imports: [MatIconModule],
  template: `
    @if (icon(); as iconName) {
      <mat-icon aria-hidden="true">{{ iconName }}</mat-icon>
    } @else {
      <span class="dot" aria-hidden="true"></span>
    }
    <span class="label">{{ label() }}</span>
  `,
  styleUrl: './status-chip.scss',
  host: {
    '[class]': '"tone-" + tone()',
    '[attr.title]': 'title() ?? null',
  },
})
export class StatusChip {
  readonly label = input.required<string>();
  readonly tone = input<Tone>('neutral');
  /** A Material Symbols name shown instead of the dot. */
  readonly icon = input<string>();
  /** Extra text on hover, e.g. who claimed an issue. */
  readonly title = input<string>();
}
