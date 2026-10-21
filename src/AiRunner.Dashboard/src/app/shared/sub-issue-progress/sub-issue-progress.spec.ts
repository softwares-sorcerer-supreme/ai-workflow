import { TestBed } from '@angular/core/testing';
import { SubIssueProgress } from './sub-issue-progress';

describe('SubIssueProgress', () => {
  async function render(closed: number, total: number) {
    const fixture = TestBed.createComponent(SubIssueProgress);
    fixture.componentRef.setInput('progress', { closed, total });
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  it('shows "closed/total" and a bar as wide as the closed share', async () => {
    const element = await render(1, 4);

    expect(element.querySelector('.text')?.textContent).toBe('1/4');
    expect((element.querySelector('.fill') as HTMLElement).style.width).toBe('25%');

    const bar = element.querySelector('[role="progressbar"]') as HTMLElement;
    expect(bar.getAttribute('aria-valuenow')).toBe('1');
    expect(bar.getAttribute('aria-valuemax')).toBe('4');
  });

  it('shows an empty bar when the total is zero', async () => {
    const element = await render(0, 0);

    expect((element.querySelector('.fill') as HTMLElement).style.width).toBe('0%');
  });
});
