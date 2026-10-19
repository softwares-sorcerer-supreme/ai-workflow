import { TestBed } from '@angular/core/testing';
import { StatusChip } from './status-chip';

describe('StatusChip', () => {
  it('shows its label with a dot in the given tone', async () => {
    const fixture = TestBed.createComponent(StatusChip);
    fixture.componentRef.setInput('label', 'Failed');
    fixture.componentRef.setInput('tone', 'danger');
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent?.trim()).toBe('Failed');
    expect(element.classList).toContain('tone-danger');
    expect(element.querySelector('.dot')).not.toBeNull();
  });

  it('shows an icon instead of the dot, and a hover title', async () => {
    const fixture = TestBed.createComponent(StatusChip);
    fixture.componentRef.setInput('label', 'Claimed');
    fixture.componentRef.setInput('icon', 'lock');
    fixture.componentRef.setInput('title', 'Claimed by laptop-2');
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('mat-icon')?.textContent).toBe('lock');
    expect(element.querySelector('.dot')).toBeNull();
    expect(element.getAttribute('title')).toBe('Claimed by laptop-2');
    expect(element.classList).toContain('tone-neutral');
  });
});
