import { formatNumber } from '@angular/common';
import { Component, DestroyRef, ElementRef, LOCALE_ID, computed, inject, input, signal } from '@angular/core';
import { MatTooltipModule } from '@angular/material/tooltip';
import { CostRowResponse } from '../../api/models/cost-row-response';

/** `columns`: vertical bars over time (days). `bars`: horizontal bars, one per group, longest first. */
export type CostChartLayout = 'columns' | 'bars';

/** One painted bar: its path, its hit area and the texts around it. All numbers are pixels. */
interface ChartMark {
  key: string;
  path: string;
  hit: { x: number; y: number; width: number; height: number };
  tooltip: string;
  /** Bars only: the group name above the bar and the value at its tip. */
  label?: { text: string; full: string; x: number; y: number };
  value?: { text: string; x: number; y: number };
}

interface ChartGeometry {
  width: number;
  height: number;
  marks: ChartMark[];
  /** Hairlines: the baseline, and for columns the line of the largest value. */
  lines: { x1: number; y1: number; x2: number; y2: number }[];
  texts: { text: string; x: number; y: number; anchor: 'start' | 'middle' | 'end' }[];
}

/** Width used until the element is measured (and in tests, where nothing has a size). */
const FALLBACK_WIDTH = 640;
const BAR_MAX = 24;
const RADIUS = 4;
/** Rough width of one character of the chart's 12 px text, to shorten a long group name instead of clipping it. */
const CHAR_WIDTH = 7;

const COLUMN_PLOT_HEIGHT = 180;
const COLUMN_TOP = 20;
const COLUMN_AXIS = 24;
/** The least room left of the columns for the value labels; more when the largest value needs it. */
const COLUMN_LEFT_MIN = 40;

const BAR_ROW = 40;
const BAR_THICKNESS = 14;
/** The least room right of the longest bar for its value; more when the formatted value needs it. */
const BAR_VALUE_ROOM_MIN = 56;

/** How many groups the horizontal chart shows; the table below lists all of them. */
export const MAX_BARS = 15;

let nextId = 0;

/**
 * One simple bar chart of what Copilot billed (`nanoAiu`, shown as sent, never converted) for the cost screen. It
 * only turns the API's numbers into pixels and formats them with thousands separators: the rows
 * come ordered and labelled by the API, nothing is summed here. Single series, so no legend; the card title names
 * what is plotted and the table under it is the text alternative. Inline SVG measured to the card's real width, so
 * text never scales.
 */
@Component({
  selector: 'app-cost-chart',
  imports: [MatTooltipModule],
  template: `
    @let chart = geometry();
    <svg
      role="img"
      [attr.width]="chart.width"
      [attr.height]="chart.height"
      [attr.viewBox]="'0 0 ' + chart.width + ' ' + chart.height"
      [attr.aria-labelledby]="titleId + ' ' + descId"
    >
      <title [attr.id]="titleId">{{ heading() }}</title>
      <desc [attr.id]="descId">{{ description() }}</desc>

      @for (line of chart.lines; track $index) {
        <line class="grid" [attr.x1]="line.x1" [attr.y1]="line.y1" [attr.x2]="line.x2" [attr.y2]="line.y2" />
      }
      @for (text of chart.texts; track $index) {
        <text class="axis" [attr.x]="text.x" [attr.y]="text.y" [attr.text-anchor]="text.anchor">{{ text.text }}</text>
      }
      @for (mark of chart.marks; track mark.key) {
        <g class="mark" [matTooltip]="mark.tooltip" matTooltipClass="app-pre-line">
          <rect
            class="hit"
            [attr.x]="mark.hit.x"
            [attr.y]="mark.hit.y"
            [attr.width]="mark.hit.width"
            [attr.height]="mark.hit.height"
          />
          @if (mark.path) {
            <path class="bar" [attr.d]="mark.path" />
          }
          @if (mark.label; as label) {
            <text class="label" [attr.x]="label.x" [attr.y]="label.y">
              {{ label.text }}<title>{{ label.full }}</title>
            </text>
          }
          @if (mark.value; as value) {
            <text class="value" [attr.x]="value.x" [attr.y]="value.y">{{ value.text }}</text>
          }
        </g>
      }
    </svg>
  `,
  styles: `
    :host {
      display: block;
      width: 100%;
      overflow: hidden;
    }

    svg {
      display: block;
    }

    .grid {
      stroke: var(--mat-sys-outline-variant);
      stroke-width: 1;
      shape-rendering: crispEdges;
    }

    .axis,
    .label,
    .value {
      font: var(--mat-sys-body-small);
      fill: var(--mat-sys-on-surface-variant);
      font-variant-numeric: tabular-nums;
    }

    .label {
      fill: var(--mat-sys-on-surface);
    }

    .bar {
      fill: var(--mat-sys-primary);
      transition: opacity 120ms;
    }

    .hit {
      fill: transparent;
    }

    .mark:hover .bar {
      opacity: 0.75;
    }
  `,
})
export class CostChart {
  readonly rows = input.required<readonly CostRowResponse[]>();
  readonly layout = input<CostChartLayout>('columns');
  /** The chart's accessible name, e.g. "Billed (nano AIU) - By day". */
  readonly heading = input.required<string>();
  /** One sentence for screen readers: what the bars show and where the numbers are. */
  readonly description = input('Bars of what Copilot billed, in nano AIU. The table below lists every value.');

  protected readonly titleId = `cost-chart-title-${nextId}`;
  protected readonly descId = `cost-chart-desc-${nextId++}`;

  private readonly width = signal(FALLBACK_WIDTH);
  private readonly locale = inject(LOCALE_ID);

  protected readonly geometry = computed<ChartGeometry>(() => {
    const chart: ChartInput = {
      rows: this.rows(),
      width: this.width(),
      format: (value) => formatNumber(value, this.locale, '1.0-0'),
    };

    return this.layout() === 'bars' ? barsOf(chart) : columnsOf(chart);
  });

  constructor() {
    const host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;

    if (typeof ResizeObserver === 'undefined') {
      return;
    }

    const observer = new ResizeObserver((entries) => {
      const measured = Math.floor(entries[0]?.contentRect.width ?? 0);

      if (measured > 0) {
        this.width.set(measured);
      }
    });

    observer.observe(host);
    inject(DestroyRef).onDestroy(() => observer.disconnect());
  }
}

/** What one layout needs: the API's rows, the measured width and the number format of the app's locale. */
interface ChartInput {
  rows: readonly CostRowResponse[];
  width: number;
  format: (value: number) => string;
}

/** The plotted value of a group: what Copilot billed, as the API sent it. */
function valueOf(row: CostRowResponse): number {
  return row.nanoAiu;
}

/** The tooltip of one group: the API's label and its numbers. */
function tooltipOf(row: CostRowResponse, format: ChartInput['format']): string {
  return [
    row.label,
    `Billed: ${format(row.nanoAiu)} nano AIU`,
    `Calls: ${format(row.calls)}`,
    `Jobs: ${format(row.jobs)}`,
  ].join('\n');
}

/** The largest value sets the scale; at least 1 so a chart of zeros draws no bars instead of dividing by zero. */
function scaleMax(rows: readonly CostRowResponse[]): number {
  return Math.max(1, ...rows.map(valueOf));
}

/** Rough pixels a line of the chart's 12 px text takes. */
function textWidth(text: string): number {
  return text.length * CHAR_WIDTH;
}

/** Vertical bars, oldest day on the left; the first and last day under the axis, the largest value on the left. */
function columnsOf({ rows, width, format }: ChartInput): ChartGeometry {
  const max = scaleMax(rows);
  const maxText = format(max);
  // Billed values run into the millions: the left axis widens so its label is never clipped.
  const columnLeft = Math.max(COLUMN_LEFT_MIN, textWidth(maxText) + 10);
  const plotWidth = Math.max(1, width - columnLeft);
  const slot = plotWidth / Math.max(1, rows.length);
  const barWidth = Math.max(1, Math.min(BAR_MAX, slot - 2));
  const baseline = COLUMN_TOP + COLUMN_PLOT_HEIGHT;
  const height = baseline + COLUMN_AXIS;

  const marks = rows.map((row, index) => {
    const slotX = columnLeft + index * slot;
    const x = slotX + (slot - barWidth) / 2;
    const barHeight = (valueOf(row) / max) * COLUMN_PLOT_HEIGHT;

    return {
      key: row.key,
      path: barHeight > 0 ? columnPath(x, baseline, barWidth, barHeight) : '',
      hit: { x: slotX, y: COLUMN_TOP, width: slot, height: COLUMN_PLOT_HEIGHT },
      tooltip: tooltipOf(row, format),
    };
  });

  const texts: ChartGeometry['texts'] = [
    { text: maxText, x: columnLeft - 6, y: COLUMN_TOP + 4, anchor: 'end' },
    { text: '0', x: columnLeft - 6, y: baseline + 4, anchor: 'end' },
  ];

  const first = rows.at(0);
  const last = rows.at(-1);
  if (first) {
    texts.push({ text: first.label, x: columnLeft, y: height - 6, anchor: 'start' });
  }

  if (last && rows.length > 1) {
    texts.push({ text: last.label, x: width, y: height - 6, anchor: 'end' });
  }

  return {
    width,
    height,
    marks,
    lines: [
      { x1: columnLeft, y1: COLUMN_TOP, x2: width, y2: COLUMN_TOP },
      { x1: columnLeft, y1: baseline, x2: width, y2: baseline },
    ],
    texts,
  };
}

/** Horizontal bars in the API's order (most billed first): the name above each bar, the value at its tip. */
function barsOf({ rows, width, format }: ChartInput): ChartGeometry {
  const shown = rows.slice(0, MAX_BARS);
  const max = scaleMax(shown);
  const valueRoom = Math.max(BAR_VALUE_ROOM_MIN, textWidth(format(max)) + 12);
  const plotWidth = Math.max(1, width - valueRoom);
  const maxChars = Math.max(4, Math.floor(width / CHAR_WIDTH));

  const marks = shown.map((row, index) => {
    const top = index * BAR_ROW;
    const barY = top + BAR_ROW - BAR_THICKNESS - 6;
    const barLength = (valueOf(row) / max) * plotWidth;
    const text = row.label.length > maxChars ? `${row.label.slice(0, maxChars - 3)}...` : row.label;

    return {
      key: row.key,
      path: barLength > 0 ? barPath(0, barY, barLength, BAR_THICKNESS) : '',
      hit: { x: 0, y: top, width, height: BAR_ROW },
      tooltip: tooltipOf(row, format),
      label: { text, full: row.label, x: 0, y: top + 14 },
      value: { text: format(valueOf(row)), x: barLength + 6, y: barY + BAR_THICKNESS - 2 },
    };
  });

  const height = Math.max(BAR_ROW, shown.length * BAR_ROW);

  return {
    width,
    height,
    marks,
    lines: [{ x1: 0.5, y1: 0, x2: 0.5, y2: height }],
    texts: [],
  };
}

/** A column from the baseline up: square at the baseline, 4 px rounded top corners. */
export function columnPath(x: number, baseline: number, width: number, height: number): string {
  const radius = Math.min(RADIUS, width / 2, height);
  const top = baseline - height;

  return [
    `M${x},${baseline}`,
    `V${top + radius}`,
    `Q${x},${top} ${x + radius},${top}`,
    `H${x + width - radius}`,
    `Q${x + width},${top} ${x + width},${top + radius}`,
    `V${baseline}`,
    'Z',
  ].join(' ');
}

/** A bar from the left axis: square at the axis, 4 px rounded right corners. */
export function barPath(x: number, y: number, length: number, thickness: number): string {
  const radius = Math.min(RADIUS, thickness / 2, length);
  const end = x + length;

  return [
    `M${x},${y}`,
    `H${end - radius}`,
    `Q${end},${y} ${end},${y + radius}`,
    `V${y + thickness - radius}`,
    `Q${end},${y + thickness} ${end - radius},${y + thickness}`,
    `H${x}`,
    'Z',
  ].join(' ');
}
