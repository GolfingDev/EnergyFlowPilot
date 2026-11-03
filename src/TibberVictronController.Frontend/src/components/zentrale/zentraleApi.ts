/** Small fetch helpers for the Zentrale views. Error texts are German because they reach the UI unchanged. */
export class ApiRequestError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

async function readError(response: Response, fallback: string): Promise<ApiRequestError> {
  const body = await response.json().catch(() => null) as { message?: string; Message?: string; detail?: string } | null;
  const text = body?.message ?? body?.Message ?? body?.detail;
  if (response.status === 403) return new ApiRequestError('Nur mit der Rolle Admin möglich.', 403);
  return new ApiRequestError(text ?? `${fallback} (HTTP ${response.status}).`, response.status);
}

export async function getJson<T>(url: string, fallback = 'Daten konnten nicht geladen werden'): Promise<T> {
  const response = await fetch(url);
  if (!response.ok) throw await readError(response, fallback);
  return await response.json() as T;
}

export async function sendJson<T>(method: 'POST' | 'PUT' | 'DELETE', url: string, body?: unknown, fallback = 'Befehl wurde nicht ausgeführt'): Promise<T | null> {
  const response = await fetch(url, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  if (!response.ok) throw await readError(response, fallback);
  return await response.json().catch(() => null) as T | null;
}

export function errorText(reason: unknown, fallback: string): string {
  return reason instanceof Error && reason.message ? reason.message : fallback;
}
