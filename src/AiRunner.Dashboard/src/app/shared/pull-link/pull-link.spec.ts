import { TestBed } from '@angular/core/testing';
import { PullResponse } from '../../api/models/pull-response';
import { samplePull } from '../testing';
import { PullLink } from './pull-link';

describe('PullLink', () => {
  async function render(pull: PullResponse) {
    const fixture = TestBed.createComponent(PullLink);
    fixture.componentRef.setInput('pull', pull);
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  it('links "#number" to the PR on GitBucket in a new tab', async () => {
    const element = await render(samplePull());

    const link = element.querySelector('a') as HTMLAnchorElement;
    expect(link.textContent?.trim()).toBe('#50');
    expect(link.href).toBe('http://gitbucket.local/team/shop/pull/50');
    expect(link.target).toBe('_blank');
    expect(link.getAttribute('aria-label')).toBe('Pull request #50, Open');
  });

  it.each([
    ['open', 'tone-info'],
    ['merged', 'tone-success'],
    ['closed', 'tone-neutral'],
  ])('colors a %s PR with %s', async (state, toneClass) => {
    const element = await render(samplePull({ state }));

    expect(element.classList).toContain(toneClass);
  });

  it('shows plain text when the API sends no link', async () => {
    const element = await render(samplePull({ htmlUrl: null, state: 'merged', stateLabel: 'Merged' }));

    expect(element.querySelector('a')).toBeNull();
    expect(element.querySelector('span')?.getAttribute('title')).toBe('team/shop #50 Export orders as CSV (Merged)');
  });
});
