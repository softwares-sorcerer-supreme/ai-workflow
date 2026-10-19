import { Injectable, inject } from '@angular/core';
import { Api } from '../api/api';
import { getHealth } from '../api/fn/health/get-health';
import { HealthResponse } from '../api/models/health-response';
import { PollResource, pollResource } from '../shared/poll-resource';

/**
 * The runner's health, polled once for the whole app: the health strip and any screen that shows health read
 * this, so `/api/health` is never polled twice.
 */
@Injectable({ providedIn: 'root' })
export class HealthStore {
  private readonly api = inject(Api);

  readonly health: PollResource<HealthResponse> = pollResource(() => this.api.invoke(getHealth));
}
