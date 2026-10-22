import { TestBed } from '@angular/core/testing';
import { AgentDiff, parseDiff } from './agent-diff';

const DIFF = [
  'diff --git a/agents/review.agent.md b/agents/review.agent.md',
  'index 1111111..2222222 100644',
  '--- a/agents/review.agent.md',
  '+++ b/agents/review.agent.md',
  '@@ -1,3 +1,3 @@',
  ' ---',
  '-name: old',
  '+name: review',
  '',
].join('\n');

describe('parseDiff', () => {
  it('gives each line of a unified diff its kind and keeps the text', () => {
    expect(parseDiff(DIFF)).toEqual([
      { kind: 'meta', text: 'diff --git a/agents/review.agent.md b/agents/review.agent.md' },
      { kind: 'meta', text: 'index 1111111..2222222 100644' },
      { kind: 'meta', text: '--- a/agents/review.agent.md' },
      { kind: 'meta', text: '+++ b/agents/review.agent.md' },
      { kind: 'hunk', text: '@@ -1,3 +1,3 @@' },
      { kind: 'context', text: ' ---' },
      { kind: 'remove', text: '-name: old' },
      { kind: 'add', text: '+name: review' },
    ]);
  });

  it('gives no lines for an empty diff', () => {
    expect(parseDiff('')).toEqual([]);
  });
});

describe('AgentDiff', () => {
  async function render(diff: string): Promise<HTMLElement> {
    await TestBed.configureTestingModule({ imports: [AgentDiff] }).compileComponents();

    const fixture = TestBed.createComponent(AgentDiff);
    fixture.componentRef.setInput('diff', diff);
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  it('draws added, removed and hunk lines with their own class', async () => {
    const element = await render(DIFF);
    const lines = [...element.querySelectorAll('[data-testid="agent-diff"] .line')];

    expect(lines.length).toBe(8);
    expect(element.querySelector('.line.add')?.textContent?.trim()).toBe('+name: review');
    expect(element.querySelector('.line.remove')?.textContent?.trim()).toBe('-name: old');
    expect(element.querySelector('.line.hunk')?.textContent?.trim()).toBe('@@ -1,3 +1,3 @@');
    expect(element.querySelectorAll('.line.meta').length).toBe(4);
  });

  it('says there are no changes when the diff is empty', async () => {
    const element = await render('');

    expect(element.querySelector('[data-testid="agent-diff"]')).toBeNull();
    expect(element.textContent).toContain('No changes against git');
  });
});
