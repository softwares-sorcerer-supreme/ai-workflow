import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { InlineNote } from './inline-note';

@Component({
  imports: [InlineNote],
  template: `<app-inline-note icon="sync_problem">Showing older data.</app-inline-note>`,
})
class Host {}

describe('InlineNote', () => {
  it('shows its text with an icon in the warning tone by default', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();

    const note = (fixture.nativeElement as HTMLElement).querySelector(
      'app-inline-note',
    ) as HTMLElement;
    expect(note.classList).toContain('tone-warning');
    expect(note.querySelector('mat-icon')?.textContent).toBe('sync_problem');
    expect(note.querySelector('.text')?.textContent).toBe('Showing older data.');
  });
});
