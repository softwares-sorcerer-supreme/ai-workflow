import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Tone } from '../tone';
import { CountToggle, CountToggleLook } from './count-toggle';

@Component({
  imports: [CountToggle],
  template: `
    <button appCountToggle [label]="label" [count]="count()" [tone]="tone()" [pressed]="pressed()" [look]="look()">
    </button>
  `,
})
class Host {
  readonly label = 'Needs you';
  readonly count = signal<number | undefined>(1234);
  readonly tone = signal<Tone | undefined>('warning');
  readonly pressed = signal(false);
  readonly look = signal<CountToggleLook>('chip');
}

describe('CountToggle', () => {
  async function render() {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();

    const button = (fixture.nativeElement as HTMLElement).querySelector('button') as HTMLButtonElement;

    return { fixture, button };
  }

  it('shows the label, the count with separators, the tone and the look on the button itself', async () => {
    const { button } = await render();

    expect(button.type).toBe('button');
    expect(button.querySelector('.label')?.textContent?.trim()).toBe('Needs you');
    expect(button.querySelector('.count')?.textContent).toBe('1,234');
    expect(button.querySelector('.dot')).not.toBeNull();
    expect(button.classList).toContain('tone-warning');
    expect(button.classList).toContain('chip');
  });

  it('marks the pressed state for the eye and for assistive technology', async () => {
    const { fixture, button } = await render();

    expect(button.getAttribute('aria-pressed')).toBe('false');

    fixture.componentInstance.pressed.set(true);
    await fixture.whenStable();

    expect(button.getAttribute('aria-pressed')).toBe('true');
    expect(button.classList).toContain('selected');
  });

  it('leaves out the dot without a tone and the number without a count', async () => {
    const { fixture, button } = await render();

    fixture.componentInstance.tone.set(undefined);
    fixture.componentInstance.count.set(undefined);
    fixture.componentInstance.look.set('segment');
    await fixture.whenStable();

    expect(button.querySelector('.dot')).toBeNull();
    expect(button.querySelector('.count')).toBeNull();
    expect(button.classList).toContain('tone-neutral');
    expect(button.classList).toContain('segment');
  });
});
