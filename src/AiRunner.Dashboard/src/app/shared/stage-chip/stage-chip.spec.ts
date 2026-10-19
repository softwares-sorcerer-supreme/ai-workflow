import { TestBed } from '@angular/core/testing';
import { STAGE_TONES, StageChip } from './stage-chip';

describe('StageChip', () => {
  async function render(code: string, label: string) {
    const fixture = TestBed.createComponent(StageChip);
    fixture.componentRef.setInput('code', code);
    fixture.componentRef.setInput('label', label);
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    const chip = element.querySelector('app-status-chip') as HTMLElement;

    return { element, chip };
  }

  it('shows the label from the API, not the code', async () => {
    const { chip } = await render('broken-down', 'Broken down (2/5)');

    expect(chip.textContent?.trim()).toBe('Broken down (2/5)');
  });

  it.each([
    ['pr-open', 'tone-info'],
    ['implementing', 'tone-progress'],
    ['questions-open', 'tone-warning'],
    ['done', 'tone-success'],
    ['new', 'tone-neutral'],
  ])('colors stage %s with %s', async (code, toneClass) => {
    const { chip } = await render(code, 'x');

    expect(chip.classList).toContain(toneClass);
  });

  it('shows an unknown stage code as neutral instead of failing', async () => {
    const { chip, element } = await render('a-new-stage', 'A new stage');

    expect(chip.classList).toContain('tone-neutral');
    expect(element.getAttribute('data-stage')).toBe('a-new-stage');
  });

  it('has a color for every stage of the spec', () => {
    const specStages = [
      'done',
      'closed-no-merge',
      'merged',
      'pr-open',
      'broken-down',
      'implementing',
      'ready-complex',
      'ready-simple',
      'questions-open',
      'new',
    ];

    expect(Object.keys(STAGE_TONES).sort()).toEqual(specStages.sort());
  });
});
