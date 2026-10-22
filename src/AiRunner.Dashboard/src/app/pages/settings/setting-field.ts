import { Component, computed, input, linkedSignal, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { RouterLink } from '@angular/router';
import { EffectiveValue } from '../../api/models/effective-value';
import { SETUP_PATH } from '../../layout/nav-items';
import { StatusChip } from '../../shared/status-chip/status-chip';
import {
  JsonValue,
  MapRow,
  SettingField,
  fallbackFlag,
  fallbackNote,
  formatValue,
  isMap,
  mapRows,
  parseInteger,
  parseLines,
  rowsToMap,
} from './settings-form';

/** How a field is edited, from the schema's `type`, `allowedValues` and `isSecret`. */
type FieldKind =
  | 'secret'
  | 'select'
  | 'text'
  | 'number'
  | 'toggle'
  | 'multi-select'
  | 'lines'
  | 'map'
  | 'unknown';

/**
 * One key of repos.yml: the control for its schema type, its help, what applies when the scope does not write it,
 * a "restart needed" chip, and a reset button that removes the key (inherited or built in again). It never changes
 * the file itself: it emits the new value (`undefined` = remove the key) and the screen updates its draft.
 */
@Component({
  selector: 'app-setting-field',
  imports: [
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatSlideToggleModule,
    RouterLink,
    StatusChip,
  ],
  templateUrl: './setting-field.html',
  styleUrl: './setting-field.scss',
  host: { '[attr.data-field]': 'field().id' },
})
export class SettingFieldEditor {
  readonly field = input.required<SettingField>();
  /** The value the draft writes for this key; undefined = not written. */
  readonly value = input<JsonValue | undefined>(undefined);
  /** The API's merged value of a project key (`effective`), when it has one. */
  readonly effective = input<EffectiveValue | undefined>(undefined);

  /** The new value; `undefined` removes the key. */
  readonly valueChange = output<JsonValue | undefined>();
  /** True while the typed text is not a valid value (a number field holding letters). */
  readonly invalidChange = output<boolean>();

  protected readonly setupPath = SETUP_PATH;

  protected readonly kind = computed<FieldKind>(() => {
    const definition = this.field().definition;
    const hasChoices = (definition.allowedValues?.length ?? 0) > 0;

    if (definition.isSecret) {
      return 'secret';
    }

    switch (definition.type) {
      case 'string':
        return hasChoices ? 'select' : 'text';
      case 'integer':
        return 'number';
      case 'boolean':
        return 'toggle';
      case 'string-list':
        return hasChoices ? 'multi-select' : 'lines';
      case 'string-map':
        return 'map';
      default:
        return 'unknown';
    }
  });

  protected readonly isWritten = computed(() => this.value() !== undefined);

  /** A project key the API merges with defaults: its reset brings back the inherited value. */
  protected readonly isInheritable = computed(() => this.effective() !== undefined);

  protected readonly resetLabel = computed(() =>
    this.isInheritable() ? 'Reset to inherited' : 'Reset to default',
  );

  protected readonly note = computed(() => {
    if (this.isWritten()) {
      return this.isInheritable() ? 'Set in this project: overrides the defaults.' : undefined;
    }

    return fallbackNote(this.field().definition, this.effective());
  });

  protected readonly textValue = computed(() => {
    const value = this.value();

    return typeof value === 'string' || typeof value === 'number' ? String(value) : '';
  });

  protected readonly listValue = computed(() => {
    const value = this.value();

    return Array.isArray(value) ? value.filter((entry) => typeof entry === 'string') : [];
  });

  protected readonly linesValue = computed(() => this.listValue().join('\n'));

  protected readonly checked = computed(() => {
    const value = this.value();
    if (typeof value === 'boolean') {
      return value;
    }

    return fallbackFlag(this.field().definition, this.effective());
  });

  /** The placeholder of an empty text, number or list box: the value that applies. */
  protected readonly placeholder = computed(() => {
    const effective = this.effective();
    if (effective !== undefined) {
      const inherited = effective.inherited.value;

      return inherited === null || inherited === undefined ? '' : formatValue(inherited as JsonValue);
    }

    const fallback = this.field().definition.default;

    return fallback === undefined || fallback === null ? '' : formatValue(fallback as JsonValue);
  });

  protected readonly isSecretSet = computed(() => {
    const value = this.value();

    return isMap(value) && value['isSet'] === true;
  });

  protected readonly rawJson = computed(() => JSON.stringify(this.value() ?? null));

  /**
   * The lines of a key/value editor: the written map, plus lines added here that have no name yet (they are not in
   * the file until they get one). A new value from the draft starts over from the map.
   */
  protected readonly rows = linkedSignal<MapRow[]>(() => mapRows(this.value()));

  protected onText(event: Event): void {
    const text = (event.target as HTMLInputElement).value;
    this.valueChange.emit(text === '' ? undefined : text);
  }

  protected onNumber(event: Event): void {
    const text = (event.target as HTMLInputElement).value;
    if (text.trim() === '') {
      this.invalidChange.emit(false);
      this.valueChange.emit(undefined);

      return;
    }

    const number = parseInteger(text);
    this.invalidChange.emit(number === undefined);

    if (number !== undefined) {
      this.valueChange.emit(number);
    }
  }

  protected onLines(event: Event): void {
    const text = (event.target as HTMLTextAreaElement).value;
    this.valueChange.emit(parseLines(text));
  }

  protected addRow(): void {
    this.rows.update((rows) => [...rows, { name: '', value: '' }]);
  }

  protected removeRow(index: number): void {
    this.updateRows(this.rows().filter((_row, position) => position !== index));
  }

  protected setRowName(index: number, event: Event): void {
    const name = (event.target as HTMLInputElement).value;
    this.updateRows(this.rows().map((row, position) => (position === index ? { ...row, name } : row)));
  }

  protected setRowValue(index: number, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.updateRows(this.rows().map((row, position) => (position === index ? { ...row, value } : row)));
  }

  /** Keeps the lines and emits the map they mean, but only when it differs, so a line without a name stays. */
  private updateRows(rows: MapRow[]): void {
    this.rows.set(rows);

    const next = rowsToMap(rows);
    const isSame = JSON.stringify(next ?? null) === JSON.stringify(this.value() ?? null);

    if (!isSame) {
      this.valueChange.emit(next);
    }
  }

  protected reset(): void {
    this.invalidChange.emit(false);
    this.valueChange.emit(undefined);
  }
}
