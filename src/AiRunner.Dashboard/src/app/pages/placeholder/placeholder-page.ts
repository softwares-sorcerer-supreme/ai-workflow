import { Component, input } from '@angular/core';
import { PageHeader } from '../../shared/page-header/page-header';
import { ViewState } from '../../shared/view-state/view-state';

/**
 * A screen that is not built yet. Its title and text come from the route's data (bound as inputs). Each later
 * slice replaces its route's component with the real screen.
 */
@Component({
  selector: 'app-placeholder-page',
  imports: [PageHeader, ViewState],
  template: `
    <app-page-header [title]="heading()" [subtitle]="subtitle()" />
    <section class="app-card">
      <app-view-state kind="empty" icon="construction" title="Not built yet" [message]="message()" />
    </section>
  `,
})
export class PlaceholderPage {
  readonly heading = input('');
  readonly subtitle = input<string>();
  readonly message = input('This screen comes in a later version of the dashboard.');
}
