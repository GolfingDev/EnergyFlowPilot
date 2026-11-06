// Push notifications of this device: the browser subscription lives here, the backend only stores where to send.

export type PushState = 'unsupported' | 'blocked' | 'off' | 'on';

export function registerServiceWorker(): void {
  if (!('serviceWorker' in navigator)) return;
  // The worker only shows notifications and caches nothing; registering it also makes the app installable.
  window.addEventListener('load', () => void navigator.serviceWorker.register('/sw.js').catch(() => undefined));
}

export function pushSupported(): boolean {
  return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window && window.isSecureContext;
}

async function registration(): Promise<ServiceWorkerRegistration> {
  return (await navigator.serviceWorker.getRegistration('/')) ?? navigator.serviceWorker.register('/sw.js');
}

export async function pushState(): Promise<PushState> {
  if (!pushSupported()) return 'unsupported';
  if (Notification.permission === 'denied') return 'blocked';
  // "On" only when the server knows this device too; otherwise the page offers to switch on again.
  return await syncPush() ? 'on' : 'off';
}

function sameKey(subscription: PushSubscription, publicKey: string): boolean {
  const own = subscription.options.applicationServerKey;
  if (!own) return false;
  const expected = keyBytes(publicKey);
  const actual = new Uint8Array(own);
  return actual.length === expected.length && actual.every((value, index) => value === expected[index]);
}

function register(subscription: PushSubscription): Promise<Response> {
  const json = subscription.toJSON();
  return fetch('/api/push/subscription', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ endpoint: json.endpoint, p256dh: json.keys?.p256dh, auth: json.keys?.auth })
  });
}

/**
 * Sends this device's subscription to the server again. The server forgets a subscription the push service rejects
 * (Google answered HTTP 410 on 2026-11-05) while the browser still showed "on", so every app start re-registers it.
 * False when there is nothing usable to register: no subscription, one for an older server key, or the server refuses.
 */
export async function syncPush(): Promise<boolean> {
  if (!pushSupported() || Notification.permission !== 'granted') return false;
  const subscription = await (await registration()).pushManager.getSubscription();
  if (!subscription) return false;
  const keyResponse = await fetch('/api/push/public-key');
  if (!keyResponse.ok) return false;
  const { publicKey } = await keyResponse.json() as { publicKey: string };
  if (!sameKey(subscription, publicKey)) return false;
  return (await register(subscription)).ok;
}

function keyBytes(base64Url: string): Uint8Array<ArrayBuffer> {
  const base64 = base64Url.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(base64Url.length / 4) * 4, '=');
  const raw = atob(base64);
  const bytes = new Uint8Array(new ArrayBuffer(raw.length));
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
  return bytes;
}

async function failure(response: Response, fallback: string): Promise<Error> {
  const body = await response.json().catch(() => null) as { message?: string } | null;
  return new Error(body?.message ?? `${fallback} (HTTP ${response.status}).`);
}

export async function enablePush(): Promise<void> {
  if (await Notification.requestPermission() !== 'granted') throw new Error('Benachrichtigungen sind im Browser nicht erlaubt.');
  const keyResponse = await fetch('/api/push/public-key');
  if (!keyResponse.ok) throw await failure(keyResponse, 'Push-Schlüssel nicht abrufbar');
  const { publicKey } = await keyResponse.json() as { publicKey: string };
  const worker = await registration();
  // A subscription made with an older server key would never receive anything.
  const existing = await worker.pushManager.getSubscription();
  if (existing) await existing.unsubscribe();
  const subscription = await worker.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyBytes(publicKey) });
  const response = await register(subscription);
  if (!response.ok) {
    await subscription.unsubscribe();
    throw await failure(response, 'Benachrichtigungen konnten nicht eingeschaltet werden');
  }
}

export async function disablePush(): Promise<void> {
  const subscription = await (await registration()).pushManager.getSubscription();
  if (!subscription) return;
  await fetch(`/api/push/subscription?endpoint=${encodeURIComponent(subscription.endpoint)}`, { method: 'DELETE' });
  await subscription.unsubscribe();
}

export async function testPush(): Promise<void> {
  const response = await fetch('/api/push/test', { method: 'POST' });
  if (!response.ok) throw await failure(response, 'Testnachricht nicht gesendet');
}
