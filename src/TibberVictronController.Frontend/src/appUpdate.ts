// Reloads the page by itself after a new version was deployed. The Pi updates itself from Git; without this an open
// tab (or the installed app) keeps running the old frontend until someone reloads by hand.
//
// The build writes its identity into the bundle (__APP_BUILD__) and into /version.json. The page compares the two:
// a different /version.json means a newer frontend lies on the server.

declare const __APP_BUILD__: { revision: string | null; builtAtUtc: string | null };

export type UpdateDecision = 'none' | 'wait' | 'reload';

export interface UpdateCheck {
  /** Identity of the running page; null in development, where nothing is ever reloaded. */
  currentBuildId: string | null;
  /** Identity the server offers now; null when it could not be read. */
  serverBuildId: string | null;
  /** Identity a reload was already tried for (kept for the browser session). */
  attemptedBuildId: string | null;
  /** The user is typing, has a dialog open or has unsaved changes. */
  busy: boolean;
  /** The backend answers. A deployment replaces the frontend first and restarts the backend seconds later. */
  backendHealthy: boolean;
}

export function buildIdOf(build: { revision: string | null; builtAtUtc: string | null }): string | null {
  // Development server: no build time, and the bundle changes with every save anyway.
  return build.builtAtUtc ? `${build.revision ?? 'unknown'}@${build.builtAtUtc}` : null;
}

export function decideUpdate(check: UpdateCheck): UpdateDecision {
  if (!check.currentBuildId || !check.serverBuildId || check.serverBuildId === check.currentBuildId) return 'none';
  // One reload per offered version: if the reloaded page is still the old one (a cache in between), reloading again
  // would loop forever.
  if (check.attemptedBuildId === check.serverBuildId) return 'none';
  // Never interrupt input, and never reload into a backend that is still restarting (the page would stay empty).
  return check.busy || !check.backendHealthy ? 'wait' : 'reload';
}

const checkMilliseconds = 60_000;
const retryMilliseconds = 10_000;
const attemptedKey = 'efp.update.attemptedBuildId';
const holds = new Set<string>();

/** Pages with unsaved changes hold the reload back for as long as `active` is true. */
export function holdAppUpdate(key: string, active: boolean): void {
  if (active) holds.add(key); else holds.delete(key);
}

function userIsBusy(): boolean {
  if (holds.size > 0) return true;
  // A hidden tab can always reload: nobody is looking at it.
  if (document.visibilityState === 'hidden') return false;
  const focused = document.activeElement as HTMLElement | null;
  const typing = !!focused && (['INPUT', 'TEXTAREA', 'SELECT'].includes(focused.tagName) || focused.isContentEditable);
  return typing || document.querySelector('.v-overlay--active.v-dialog, .v-overlay--active.v-menu') !== null;
}

function attempted(): string | null {
  try { return window.sessionStorage.getItem(attemptedKey); } catch { return null; }
}

async function serverBuildId(): Promise<string | null> {
  try {
    const response = await fetch(`/version.json?t=${Date.now()}`, { cache: 'no-store' });
    if (!response.ok) return null;
    const value = (await response.json()) as { buildId?: unknown };
    return typeof value.buildId === 'string' ? value.buildId : null;
  } catch {
    return null;
  }
}

async function backendHealthy(): Promise<boolean> {
  try {
    return (await fetch('/health', { cache: 'no-store' })).ok;
  } catch {
    return false;
  }
}

const noticeKey = 'efp.update.noticeShownFor';

/**
 * Short revision of the running build when this page was loaded by an automatic reload and the notice was not shown
 * yet; otherwise null. Lets the user see that the page updated itself (operator question 2026-11-15: a silent reload
 * cannot be told from no reload).
 */
export function consumeUpdateNotice(): string | null {
  const currentBuildId = buildIdOf(__APP_BUILD__);
  try {
    if (!currentBuildId || attempted() !== currentBuildId || window.sessionStorage.getItem(noticeKey) === currentBuildId) return null;
    window.sessionStorage.setItem(noticeKey, currentBuildId);
  } catch {
    return null;
  }
  return __APP_BUILD__.revision?.slice(0, 7) ?? 'unbekannt';
}

export function startAppUpdateWatcher(): void {
  const currentBuildId = buildIdOf(__APP_BUILD__);
  if (!currentBuildId) return;
  let timer: number | undefined;
  let running = false;

  const check = async (): Promise<void> => {
    if (running) return;
    running = true;
    let next = checkMilliseconds;
    try {
      const offered = await serverBuildId();
      const pending = !!offered && offered !== currentBuildId && attempted() !== offered;
      const decision = decideUpdate({
        currentBuildId, serverBuildId: offered, attemptedBuildId: attempted(), busy: userIsBusy(),
        // Asked only when a newer version is offered: no extra request in normal operation.
        backendHealthy: pending ? await backendHealthy() : true
      });
      if (decision === 'reload' && offered) {
        try { window.sessionStorage.setItem(attemptedKey, offered); } catch { /* private mode: the worst case is one extra reload */ }
        window.location.reload();
        return;
      }
      if (decision === 'wait') next = retryMilliseconds;
    } finally {
      running = false;
      window.clearTimeout(timer);
      timer = window.setTimeout(() => void check(), next);
    }
  };

  // Coming back to the tab or the installed app is the moment an old page is noticed, so check at once.
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') void check(); });
  window.addEventListener('online', () => void check());
  timer = window.setTimeout(() => void check(), retryMilliseconds);
}
