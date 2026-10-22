import { HttpInterceptorFn } from '@angular/common/http';

/** The header the runner's write guard asks for (C# `Web/WriteGuard`). A cross-site form cannot set it. */
export const WRITE_GUARD_HEADER = 'X-AiRunner-Request';

/**
 * Makes every write pass the runner's CSRF guard: a request whose method is not GET gets `X-AiRunner-Request: 1`,
 * `Content-Type: application/json` and the body `{}` when it has none (the generated POST functions send no body,
 * and the runner refuses a write without the JSON content type). GET requests go out exactly as they are. This is
 * the only place the header is set; screens never set it by hand.
 */
export const writeGuardInterceptor: HttpInterceptorFn = (request, next) => {
  if (request.method === 'GET') {
    return next(request);
  }

  const guarded = request.clone({
    setHeaders: { [WRITE_GUARD_HEADER]: '1', 'Content-Type': 'application/json' },
    body: request.body ?? {},
  });

  return next(guarded);
};
