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

self.addEventListener('notificationclick', (event) => {
  const notification = event.notification;
  const data = notification.data || {};
  notification.close();
  event.waitUntil((async () => {
    // A button of the base state of charge question answers directly; the token in the message authorises it.
    if (data.type === 'base-choice' && data.token && (event.action === 'Immediate' || event.action === 'PriceDriven')) {
      try {
        const response = await fetch('/api/push/base-choice', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ token: data.token, mode: event.action })
        });
        if (response.ok) {
          await self.registration.showNotification('Auswahl gespeichert', {
            body: event.action === 'Immediate' ? 'Das Auto lädt jetzt bis zum Grund-Ladestand.' : 'Das Auto lädt preisgesteuert.',
            tag: notification.tag,
            icon: '/icon-192.png',
            badge: '/badge-96.png'
          });
          return;
        }
      } catch {
        // Offline or expired: open the page, where the question can be answered as well.
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
