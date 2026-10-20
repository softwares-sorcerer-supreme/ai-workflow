import { TestBed } from '@angular/core/testing';
import { CostRowResponse } from '../../api/models/cost-row-response';
import { CostChart, CostChartLayout, MAX_BARS, barPath, columnPath } from './cost-chart';

/**
 * A group whose billed value is `nanoAiu`; its calls are deliberately different, so a test sees which one is plotted.
 */
function row(key: string, nanoAiu: number): CostRowResponse {
  return {
    key,
    label: key,
    calls: 1,
    jobs: 1,
    jobsWithoutCount: 0,
    jobsWithoutUsage: 0,
    durationSeconds: 10,
    nanoAiu,
    inputTokens: 0,
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
    outputTokens: 0,
  };
}

describe('CostChart', () => {
  async function render(rows: CostRowResponse[], layout: CostChartLayout) {
    TestBed.configureTestingModule({ imports: [CostChart] });

    const fixture = TestBed.createComponent(CostChart);
    fixture.componentRef.setInput('rows', rows);
    fixture.componentRef.setInput('layout', layout);
    fixture.componentRef.setInput('heading', 'Billed (nano AIU) - By day');
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  /** The height of a column path: baseline (first M) minus its top (the H line's y). */
  function columnHeight(path: Element): number {
    const d = path.getAttribute('d') ?? '';
    const baseline = Number(/^M[\d.]+,([\d.]+)/.exec(d)?.[1]);
    const top = Number(/Q[\d.]+,([\d.]+)/.exec(d)?.[1]);

    return baseline - top;
  }

  it('is an image with a title and a description for screen readers', async () => {
    const element = await render([row('2026-01-01', 2)], 'columns');

    const svg = element.querySelector('svg') as SVGElement;
    expect(svg.getAttribute('role')).toBe('img');
    expect(svg.querySelector('title')?.textContent).toBe('Billed (nano AIU) - By day');
    expect(svg.querySelector('desc')?.textContent).toContain('table below');

    const [titleId, descId] = (svg.getAttribute('aria-labelledby') ?? '').split(' ');
    expect(svg.querySelector(`#${titleId}`)).not.toBeNull();
    expect(svg.querySelector(`#${descId}`)).not.toBeNull();
  });

  it('scales columns to the largest value and draws none for zero', async () => {
    const element = await render([row('a', 4), row('b', 2), row('c', 0)], 'columns');

    const bars = Array.from(element.querySelectorAll('path.bar'));
    expect(bars.length).toBe(2);
    expect(columnHeight(bars[0])).toBeCloseTo(2 * columnHeight(bars[1]), 5);
    expect(element.querySelectorAll('rect.hit').length).toBe(3);
  });

  it('labels the largest value, zero, and the first and last day', async () => {
    const element = await render([row('2026-01-01', 9), row('2026-01-02', 3)], 'columns');

    const texts = Array.from(element.querySelectorAll('text.axis')).map((text) => text.textContent?.trim());
    expect(texts).toEqual(['9', '0', '2026-01-01', '2026-01-02']);
  });

  it('writes the billed value with thousands separators and widens the axis for it', async () => {
    const element = await render([row('2026-01-01', 1234567890), row('2026-01-02', 0)], 'columns');

    expect(element.querySelector('text.axis')?.textContent?.trim()).toBe('1,234,567,890');
    const axisX = Number(element.querySelector('text.axis')?.getAttribute('x'));
    expect(axisX).toBeGreaterThanOrEqual('1,234,567,890'.length * 7);
  });

  it('plots what Copilot billed, not the number of calls', async () => {
    const rows = [
      { ...row('few-calls', 8000), calls: 1 },
      { ...row('many-calls', 2000), calls: 50 },
    ];
    const element = await render(rows, 'bars');

    const values = Array.from(element.querySelectorAll('.value')).map((value) => value.textContent?.trim());
    expect(values).toEqual(['8,000', '2,000']);
  });

  it('draws horizontal bars with the name and the value, at most MAX_BARS of them', async () => {
    const rows = Array.from({ length: MAX_BARS + 3 }, (_, index) => row(`project-${index}`, 3000 - index));
    const element = await render(rows, 'bars');

    expect(element.querySelectorAll('g.mark').length).toBe(MAX_BARS);
    expect(element.querySelector('.label')?.textContent).toContain('project-0');
    expect(element.querySelector('.value')?.textContent?.trim()).toBe('3,000');
  });

  it('keeps the bar paths square at the baseline and rounded at the data end', () => {
    expect(columnPath(10, 100, 20, 50)).toBe('M10,100 V54 Q10,50 14,50 H26 Q30,50 30,54 V100 Z');
    expect(barPath(0, 10, 50, 14)).toBe('M0,10 H46 Q50,10 50,14 V20 Q50,24 46,24 H0 Z');
  });
});
