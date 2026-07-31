import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';

/**
 * Centralised HTTP error handling. Individual services/components should not
 * scatter try/catch around every call — this interceptor normalises the
 * console diagnostics, and callers use RxJS operators (or a resource's own
 * `error()` signal) for anything that needs to react to a failure.
 *
 * 429 from POST /api/prices/refresh is deliberately NOT treated as a generic
 * error here — it carries a `secondsRemaining` cooldown that PriceStore reads
 * and turns into UI state, not an error toast. It still passes through this
 * interceptor untouched so PriceStore's own subscriber can inspect it.
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) =>
  next(req).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status !== 429) {
        console.error(`[HTTP ${error.status}] ${req.method} ${req.url}`, error.error ?? error.message);
      }
      return throwError(() => error);
    }),
  );
