import { TestBed } from '@angular/core/testing';
import { Markdown } from './markdown';

describe('Markdown', () => {
  async function render(text: string) {
    const fixture = TestBed.createComponent(Markdown);
    fixture.componentRef.setInput('text', text);
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  it('renders Markdown as formatted text', async () => {
    const element = await render('# Plan\n\n- one\n- `two`\n\n| a | b |\n|---|---|\n| 1 | 2 |');

    expect(element.querySelector('h1')?.textContent).toBe('Plan');
    expect(element.querySelectorAll('li').length).toBe(2);
    expect(element.querySelector('code')?.textContent).toBe('two');
    expect(element.querySelector('table td')?.textContent).toBe('1');
  });

  it('shows raw HTML as text and never as an element', async () => {
    const element = await render(
      'Before <script>alert(1)</script> <img src=x onerror="alert(2)">\n\n<div onclick="x()">block</div>',
    );

    expect(element.querySelector('script')).toBeNull();
    expect(element.querySelector('img')).toBeNull();
    expect(element.querySelector('div')).toBeNull();
    expect(element.textContent).toContain('<script>alert(1)</script>');
  });

  it('leaves a javascript: link as plain text', async () => {
    const element = await render('[click](javascript:alert(1)) and [ok](https://example.com)');

    const links = [...element.querySelectorAll('a')];
    expect(links.map((link) => link.getAttribute('href'))).toEqual(['https://example.com']);
    expect(element.textContent).toContain('[click](javascript:alert(1))');
  });
});
