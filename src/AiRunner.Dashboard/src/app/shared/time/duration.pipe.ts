import { Pipe, PipeTransform } from '@angular/core';
import { formatDuration } from './time-format';

/** Seconds as "3 min 05 s". Null or undefined (an old row without the value) shows "-". */
@Pipe({ name: 'duration' })
export class DurationPipe implements PipeTransform {
  transform(seconds: number | null | undefined): string {
    if (seconds === null || seconds === undefined) {
      return '-';
    }

    return formatDuration(seconds);
  }
}
