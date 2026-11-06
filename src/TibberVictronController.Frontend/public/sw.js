// Service worker of the installable app: shows push messages and answers their buttons. It caches nothing, so the
// app always shows live data (and needs the connection, like the web page).

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));
// A fetch handler is part of what makes Chrome offer "install"; requests go to the network unchanged.
self.addEventListener('fetch', () => {});

self.addEventListener('push', (event) => {
  let message = { title: 'EnergyFlowPilot', body: '', tag: 'efp', url: '/', actions: [] };
  try {
    if (event.data) message = { ...message, ...event.data.json() };
  } catch {
    // Unreadable message: still show something, browsers require a visible notification for every push.
  }

  event.waitUntil(self.registration.showNotification(message.title, {
    body: message.body,
    tag: message.tag,
    renotify: true,
    requireInteraction: message.type === 'base-choice',
    icon: '/icon-192.png',
    badge: '/badge-96.png',
    actions: (message.actions || []).map((action) => ({ action: action.action, title: action.title })),
    data: { url: message.url || '/', token: message.token || null, type: message.type || null }
  }));
});

// The browser may replace a subscription (expiry, renewal at the push service). Without passing the new one on, the
// server keeps sending to the dead address and the push service answers HTTP 410 (seen 2026-11-05).
self.addEventListener('pushsubscriptionchange', (event) => {
  event.waitUntil((async () => {
    let subscription = event.newSubscription;
    if (!subscription) {
      const keyResponse = await fetch('/api/push/public-key');
      if (!keyResponse.ok) return;
      const { publicKey } = await keyResponse.json();
      subscription = await self.registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: publicKey });
    }
    const json = subscription.toJSON();
    await fetch('/api/push/subscription', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ endpoint: json.endpoint, p256dh: json.keys && json.keys.p256dh, auth: json.keys && json.keys.auth })
    });
  })().catch(() => undefined));
});

const STOP = { action: 'Stop', title: 'Laden stoppen' };
const RESUME = { action: 'Resume', title: 'Fortsetzen' };
const BUTTONS = {
  Immediate: { url: '/api/push/base-choice', body: { mode: 'Immediate' }, title: 'Lädt jetzt', text: 'Das Auto lädt bis zum Grund-Ladestand.', next: [STOP] },
  PriceDriven: { url: '/api/push/base-choice', body: { mode: 'PriceDriven' }, title: 'Lädt preisgesteuert', text: 'Bis zum Grund-Ladestand in den günstigsten Stunden.', next: [STOP] },
  Stop: { url: '/api/push/charge-stop', body: { stopped: true }, title: 'Laden gestoppt', text: 'Bis „Fortsetzen“ oder zum nächsten Anstecken.', next: [RESUME] },
  Resume: { url: '/api/push/charge-stop', body: { stopped: false }, title: 'Laden fortgesetzt', text: 'Das Auto lädt wieder nach Plan.', next: [STOP] }
};

self.addEventListener('notificationclick', (event) => {
  const notification = event.notification;
  const data = notification.data || {};
  notification.close();
  event.waitUntil((async () => {
    // Buttons answer directly; the token in the message authorises them (the sign-in may have expired).
    const answer = data.token ? BUTTONS[event.action] : undefined;
    if (answer) {
      try {
        const response = await fetch(answer.url, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ token: data.token, ...answer.body })
        });
        if (response.ok) {
          // The follow-up notification offers the opposite, so charging can always be stopped or resumed from here.
          await self.registration.showNotification(answer.title, {
            body: answer.text,
            tag: notification.tag,
            icon: '/icon-192.png',
            badge: '/badge-96.png',
            actions: answer.next,
            data: { url: data.url, token: data.token, type: 'charge' }
          });
          return;
        }
      } catch {
        // Offline or expired: open the page, where the same buttons exist.
      }
    }

    const target = new URL(data.url || '/', self.location.origin).href;
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const open = windows.find((client) => client.url.startsWith(self.location.origin));
    if (open) {
      await open.focus();
      if ('navigate' in open) await open.navigate(target);
    } else {
      await self.clients.openWindow(target);
    }
  })());
});
