import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Api } from '../../api/api';
import { FakeApi, fixedClock, sampleIssue } from '../../shared/testing';
import { IssueBoard, BoardLane } from './issue-board';

describe('IssueBoard', () => {
  async function render(lanes: BoardLane[]) {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), fixedClock, { provide: Api, useValue: new FakeApi() }],
    });

    const fixture = TestBed.createComponent(IssueBoard);
    fixture.componentRef.setInput('lanes', lanes);
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  it('shows each lane with its label, count and tone, and its issues as cards in the given order', async () => {
    const element = await render([
      { code: 'todo', label: 'To do', count: 3, issues: [] },
      { code: 'finished', label: 'Done', count: 2, issues: [sampleIssue({ number: 9 }), sampleIssue({ number: 4 })] },
    ]);

    const lanes = Array.from(element.querySelectorAll<HTMLElement>('.lane'));
    expect(lanes.map((lane) => lane.querySelector('.lane-head')?.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
      'To do 3',
      'Done 2',
    ]);
    expect(lanes[1].querySelector('.lane-head')?.classList).toContain('tone-success');
    expect(Array.from(lanes[1].querySelectorAll('app-issue-card .number')).map((number) => number.textContent)).toEqual(
      ['#9', '#4'],
    );
  });

  it('says "Nothing here" in an empty lane', async () => {
    const element = await render([{ code: 'ready', label: 'Ready', count: 0, issues: [] }]);

    expect(element.querySelector('.nothing')?.textContent).toBe('Nothing here');
  });
});
