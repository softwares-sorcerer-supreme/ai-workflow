import { Component, ViewEncapsulation, computed, input } from '@angular/core';
import MarkdownIt from 'markdown-it';

/**
 * Raw HTML in the source is never rendered (`html: false` escapes it as text) and links with a `javascript:`,
 * `vbscript:`, `file:` or non-image `data:` URL are dropped by markdown-it's link check. Angular's sanitizer then
 * checks the result again when it is bound to `innerHTML`. Never use `bypassSecurityTrustHtml` here: log text is
 * written by an agent and can hold anything.
 */
const renderer = new MarkdownIt({ html: false, linkify: false, typographer: false });

/** Markdown text (agent logs, transcripts) shown as formatted text. Safe for untrusted input, see `renderer`. */
@Component({
  selector: 'app-markdown',
  template: '',
  styleUrl: './markdown.scss',
  // The rendered HTML is not part of this template, so scoped styles would not reach it; markdown.scss scopes
  // every rule under `app-markdown` instead.
  encapsulation: ViewEncapsulation.None,
  host: {
    '[innerHTML]': 'html()',
  },
})
export class Markdown {
  readonly text = input.required<string>();

  protected readonly html = computed(() => renderer.render(this.text()));
}
