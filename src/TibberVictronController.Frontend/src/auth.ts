import { computed, ref } from 'vue';

export interface CurrentUser {
  authenticationEnabled: boolean;
  name: string | null;
  email: string | null;
  roles: string[];
  isAdmin: boolean;
}

export const currentUser = ref<CurrentUser | null>(null);
// The backend enforces roles on every request; this flag only hides controls a viewer cannot use anyway.
export const isAdmin = computed(() => currentUser.value?.isAdmin ?? false);
export const roleLabel = computed(() => (isAdmin.value ? 'Admin' : 'Betrachter'));

let redirecting = false;

export function redirectToLogin(): void {
  if (redirecting) return;
  redirecting = true;
  const returnUrl = `${window.location.pathname}${window.location.search}`;
  window.location.href = `/api/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`;
}

/**
 * Wraps fetch once for the whole app: an expired session answers API calls with 401, and every component would
 * otherwise have to handle that itself.
 */
export function installSessionGuard(): void {
  const originalFetch = window.fetch.bind(window);
  window.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
    const response = await originalFetch(input, init);
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    const path = new URL(url, window.location.origin).pathname;
    if (response.status === 401 && path.startsWith('/api/') && !path.startsWith('/api/auth/')) {
      redirectToLogin();
    }
    return response;
  };
}

/** @returns false when the browser is being sent to the sign-in page. */
export async function loadCurrentUser(): Promise<boolean> {
  try {
    const response = await fetch('/api/auth/me', { cache: 'no-store' });
    if (response.status === 401) {
      redirectToLogin();
      return false;
    }
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    currentUser.value = await response.json() as CurrentUser;
  } catch {
    // Backend unreachable: still render the app so the existing error displays explain the problem. Without a known
    // user nothing is shown as editable.
    currentUser.value = null;
  }
  return true;
}

/** Logout is a form POST: the backend redirects to Entra, which a fetch call could not follow. */
export function logout(): void {
  const form = document.createElement('form');
  form.method = 'POST';
  form.action = '/api/auth/logout';
  document.body.appendChild(form);
  form.submit();
}
