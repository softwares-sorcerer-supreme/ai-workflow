import { Pipe, PipeTransform } from '@angular/core';

const UNITS = ['KB', 'MB', 'GB', 'TB'];

/** A size in bytes as "512 B", "12.4 KB", "2.1 MB", "3.0 GB". Only formats: the API sends the number of bytes. */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  let amount = bytes / 1024;
  let unit = 0;

  while (amount >= 1024 && unit < UNITS.length - 1) {
    amount = amount / 1024;
    unit++;
  }

  return `${amount.toFixed(1)} ${UNITS[unit]}`;
}

/** Bytes as "12.4 KB" (see `formatBytes`). Null or undefined (not known yet) shows "-". */
@Pipe({ name: 'bytes' })
export class BytesPipe implements PipeTransform {
  transform(bytes: number | null | undefined): string {
    if (bytes === null || bytes === undefined) {
      return '-';
    }

    return formatBytes(bytes);
  }
}
