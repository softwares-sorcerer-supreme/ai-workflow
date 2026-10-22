import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { Api } from '../api/api';
import { provideApiConfiguration } from '../api/api-configuration';
import { getHealth } from '../api/fn/health/get-health';
import { cancelJob } from '../api/fn/jobs/cancel-job';
import { pollNow } from '../api/fn/runner/poll-now';
import { sampleHealth } from './testing';
import { writeGuardInterceptor } from './write-guard.interceptor';

describe('writeGuardInterceptor', () => {
  let http: HttpTestingController;
  let api: Api;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([writeGuardInterceptor])),
        provideHttpClientTesting(),
        provideApiConfiguration(''),
      ],
    });

    http = TestBed.inject(HttpTestingController);
    api = TestBed.inject(Api);
  });

  afterEach(() => http.verify());

  it('adds the guard header, the JSON content type and an empty JSON body to a generated POST', async () => {
    const answer = api.invoke(pollNow);

    const request = http.expectOne({ method: 'POST', url: '/api/runner/poll' });
    expect(request.request.headers.get('X-AiRunner-Request')).toBe('1');
    expect(request.request.headers.get('Content-Type')).toBe('application/json');
    expect(request.request.body).toEqual({});

    request.flush({ message: 'Poll started.' }, { status: 202, statusText: 'Accepted' });
    expect(await answer).toEqual({ message: 'Poll started.' });
  });

  it('guards a POST with a path parameter too', async () => {
    const answer = api.invoke(cancelJob, { id: 9 });

    const request = http.expectOne({ method: 'POST', url: '/api/jobs/9/cancel' });
    expect(request.request.headers.get('X-AiRunner-Request')).toBe('1');

    request.flush({ message: 'Cancelling job 9.' }, { status: 202, statusText: 'Accepted' });
    await answer;
  });

  it('keeps a body that is already there', async () => {
    const answer = firstValueFrom(TestBed.inject(HttpClient).put('/api/x', { name: 'shop' }));

    const request = http.expectOne({ method: 'PUT', url: '/api/x' });
    expect(request.request.body).toEqual({ name: 'shop' });
    expect(request.request.headers.get('X-AiRunner-Request')).toBe('1');

    request.flush({});
    await answer;
  });

  it('leaves a GET exactly as it is', async () => {
    const answer = api.invoke(getHealth);

    const request = http.expectOne({ method: 'GET', url: '/api/health' });
    expect(request.request.headers.has('X-AiRunner-Request')).toBe(false);
    expect(request.request.headers.has('Content-Type')).toBe(false);
    expect(request.request.body).toBeNull();

    request.flush(sampleHealth());
    await answer;
  });
});
