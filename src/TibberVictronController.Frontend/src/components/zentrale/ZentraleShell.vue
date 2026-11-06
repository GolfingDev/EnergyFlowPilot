<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { useRoute } from 'vue-router';
import BuildInfo from '../BuildInfo.vue';
import ThemeMenuPanel from '../ThemeMenuPanel.vue';
import { currentUser, isAdmin, isDriver, logout, roleLabel } from '../../auth';
import { expertMode } from '../../uiMode';
import type { EnergyFlowThemeName } from '../../themeRegistry';
import { useZentraleData } from './useZentraleData';
import './zentrale.css';

/** App frame of the Zentrale: sidebar on desktop, icon rail on tablets, bottom bar on phones. */
const props = defineProps<{ themeName: EnergyFlowThemeName }>();
const emit = defineEmits<{ changeTheme: [name: EnergyFlowThemeName] }>();
const route = useRoute();
const data = useZentraleData();
const themeMenuOpen = ref(false);
const clockNow = ref(new Date());
let clockTimer: number | undefined;

const navigation = computed(() => [
  { name: 'dashboard', label: 'Dashboard', icon: 'mdi-view-dashboard-outline', routes: ['dashboard'] },
  { name: 'analysis', label: 'Analyse', icon: 'mdi-chart-box-outline', routes: ['analysis'] },
  { name: 'control', label: 'Steuerung', icon: 'mdi-tune-variant', routes: ['control', 'car', 'heating'] },
  { name: 'automations', label: 'Automationen', icon: 'mdi-robot-outline', routes: ['automations'] },
  ...(isAdmin.value ? [{ name: 'settings', label: 'Einstellungen', icon: 'mdi-cog-outline', routes: ['settings'] }] : [])
]);

const pages: Record<string, { title: string; subtitle: string }> = {
  dashboard: { title: 'Dashboard', subtitle: 'Aktueller Energiefluss und Systemstatus' },
  analysis: { title: 'Analyse', subtitle: 'Kosten, Einsparungen und Rückblick' },
  control: { title: 'Steuerung', subtitle: 'Akkus, E-Auto, Wärmepumpe und Netz' },
  automations: { title: 'Automationen', subtitle: 'Aktive Regeln, Gründe und Entscheidungsverlauf' },
  settings: { title: 'Einstellungen', subtitle: 'Konfiguration nach Themen, durchsuchbar' },
  heating: { title: 'Heizung', subtitle: 'Raumklima, Ventile und Warmwasser' },
  car: { title: 'E-Auto', subtitle: 'Fahrten, Ladeplan und Wallbox' }
};
const routeName = computed(() => String(route.name ?? 'dashboard'));
const page = computed(() => pages[routeName.value] ?? pages.dashboard);

const health = computed(() => {
  const errors = Object.keys(data.state.errors).length;
  if (!data.state.summary && !errors) return { label: 'Verbinde …', kind: 'pending' };
  if (data.state.status?.victronMqttLastError || errors) return { label: 'Eingeschränkt', kind: 'warning' };
  if (data.isStale.value) return { label: 'Live-Daten veraltet', kind: 'warning' };
  return { label: 'System aktiv', kind: 'ok' };
});
const dateText = computed(() => clockNow.value.toLocaleDateString('de-DE', { timeZone: 'Europe/Berlin', weekday: 'short', day: '2-digit', month: 'short' }));
const timeText = computed(() => clockNow.value.toLocaleTimeString('de-DE', { timeZone: 'Europe/Berlin', hour: '2-digit', minute: '2-digit' }));
const isDark = computed(() => props.themeName === 'zentrale');

const refreshMenuOpen = ref(false);
const refreshing = ref(false);
const lastLoadedText = computed(() => data.state.lastLoadedAtUtc
  ? new Date(data.state.lastLoadedAtUtc).toLocaleTimeString('de-DE', { timeZone: 'Europe/Berlin' })
  : '–');
const connectionText = computed(() => {
  switch (data.state.connection) {
    case 'connected': return { icon: 'mdi-lan-connect', title: 'Live-Verbindung aktiv', detail: 'Der Server schickt jede neue Messung und jede Änderung sofort.' };
    case 'connecting': return { icon: 'mdi-lan-pending', title: 'Live-Verbindung wird aufgebaut', detail: 'Bis dahin werden die Werte regelmäßig abgefragt.' };
    default: return { icon: 'mdi-lan-disconnect', title: 'Live-Verbindung getrennt', detail: 'Werte werden alle 15 s abgefragt, bis die Verbindung wieder steht.' };
  }
});
// The E3/DC is polled by the backend; even a live connection cannot make its values newer than that.
const e3dcPollingSeconds = computed(() => data.batteries.value[1]?.available ? data.setting('hagerEnergy.pollingIntervalSeconds') : null);

async function refreshNow(): Promise<void> {
  refreshing.value = true;
  try {
    await data.refreshAll();
  } finally {
    refreshing.value = false;
  }
}

function selectTheme(name: EnergyFlowThemeName): void {
  themeMenuOpen.value = false;
  emit('changeTheme', name);
}

onMounted(() => { clockTimer = window.setInterval(() => { clockNow.value = new Date(); }, 15_000); });
onBeforeUnmount(() => window.clearInterval(clockTimer));
</script>

<template>
  <v-app class="z-app">
    <div class="z-shell">
      <aside class="z-sidebar" aria-label="Hauptnavigation">
        <router-link to="/dashboard" class="z-brand" aria-label="Zum Dashboard">
          <img src="/Logo.png" alt="" class="z-brand__logo" />
          <span class="z-brand__text"><strong>EnergyFlowPilot</strong><small>Energy Flow Zentrale</small></span>
        </router-link>

        <nav class="z-nav">
          <router-link v-for="item in navigation" :key="item.name" :to="{ name: item.name }" class="z-nav__item"
            :class="{ 'z-nav__item--active': item.routes.includes(routeName) }" :aria-current="item.routes.includes(routeName) ? 'page' : undefined" :title="item.label">
            <v-icon :icon="item.icon" size="22" />
            <span>{{ item.label }}</span>
          </router-link>
        </nav>

        <div class="z-sidebar__footer">
          <div class="z-level" role="group" aria-label="Bedienebene">
            <span class="z-level__label">Bedienebene</span>
            <div class="z-level__switch">
              <button type="button" :aria-pressed="!expertMode" @click="expertMode = false" title="Standard"><v-icon icon="mdi-account-outline" size="18" /><span>Standard</span></button>
              <button type="button" :aria-pressed="expertMode" @click="expertMode = true" title="Experte"><v-icon icon="mdi-wrench-outline" size="18" /><span>Experte</span></button>
            </div>
          </div>
          <div class="z-theme">
            <button type="button" class="z-theme__toggle" :aria-label="isDark ? 'Helles Design' : 'Dunkles Design'" @click="selectTheme(isDark ? 'zentraleLight' : 'zentrale')">
              <v-icon :icon="isDark ? 'mdi-white-balance-sunny' : 'mdi-weather-night'" size="18" />
            </button>
            <v-menu v-model="themeMenuOpen" location="end bottom" offset="12" :close-on-content-click="false">
              <template #activator="{ props: menuProps }">
                <button type="button" class="z-theme__more" v-bind="menuProps" aria-label="Anderes Design wählen"><v-icon icon="mdi-palette-outline" size="18" /><span>Design</span></button>
              </template>
              <ThemeMenuPanel :active-name="themeName" @select="selectTheme" />
            </v-menu>
          </div>
        </div>
      </aside>

      <div class="z-content">
        <header class="z-header">
          <div class="z-header__title">
            <h1>{{ page.title }}</h1>
            <p>{{ page.subtitle }}</p>
            <BuildInfo />
          </div>
          <div class="z-header__meta">
            <span class="z-header__clock">{{ dateText }} <strong>{{ timeText }}</strong></span>
            <v-menu v-model="refreshMenuOpen" location="bottom end" offset="10" :close-on-content-click="false">
              <template #activator="{ props: menuProps }">
                <button type="button" class="z-health" :class="`z-health--${health.kind}`" v-bind="menuProps" role="status"
                  :aria-label="`${health.label}. ${connectionText.title}. Details`">
                  <i aria-hidden="true" />{{ health.label }}<v-icon icon="mdi-chevron-down" size="16" />
                </button>
              </template>
              <div class="z-refresh" role="group" aria-labelledby="z-refresh-title">
                <strong id="z-refresh-title"><v-icon :icon="connectionText.icon" size="18" :class="`z-refresh__icon--${data.state.connection}`" /> {{ connectionText.title }}</strong>
                <p>{{ connectionText.detail }}</p>
                <p>Letzte Daten {{ lastLoadedText }}</p>
                <p v-if="e3dcPollingSeconds">Akku 2 (E3/DC) liefert neue Werte nur alle {{ e3dcPollingSeconds }} s (Einstellung „E3/DC Polling-Intervall“).</p>
                <v-btn size="small" variant="tonal" prepend-icon="mdi-refresh" :loading="refreshing" @click="refreshNow">Jetzt aktualisieren</v-btn>
              </div>
            </v-menu>
            <span v-if="expertMode" class="z-expert-badge">Experte</span>
            <v-menu v-if="currentUser?.authenticationEnabled" location="bottom end" offset="10">
              <template #activator="{ props: menuProps }">
                <button type="button" class="z-user" v-bind="menuProps" :aria-label="`Angemeldet als ${currentUser?.name ?? ''} (${roleLabel})`">
                  <v-icon :icon="isAdmin ? 'mdi-shield-account-outline' : isDriver ? 'mdi-car-outline' : 'mdi-account-eye-outline'" size="22" />
                </button>
              </template>
              <v-list density="compact">
                <v-list-item :title="currentUser?.name ?? ''" :subtitle="`${currentUser?.email ?? ''} · ${roleLabel}`" />
                <v-list-item prepend-icon="mdi-logout" title="Abmelden" @click="logout" />
              </v-list>
            </v-menu>
          </div>
        </header>
        <main class="z-main">
          <router-view />
        </main>
      </div>
    </div>

    <nav class="z-bottom-nav" aria-label="Hauptnavigation">
      <router-link v-for="item in navigation" :key="item.name" :to="{ name: item.name }" :class="{ active: item.routes.includes(routeName) }"
        :aria-current="item.routes.includes(routeName) ? 'page' : undefined">
        <v-icon :icon="item.icon" size="22" /><span>{{ item.label }}</span>
      </router-link>
    </nav>
  </v-app>
</template>

<style scoped>
.z-app { background: var(--efp-background) !important; color: var(--efp-text); }
.z-shell { display: grid; grid-template-columns: 236px minmax(0, 1fr); min-height: 100vh; }
.z-sidebar { position: sticky; top: 0; height: 100vh; display: flex; flex-direction: column; gap: 18px; padding: 18px 12px; background: var(--efp-nav-background); border-right: 1px solid var(--efp-border); }
.z-brand { display: flex; align-items: center; gap: 10px; padding: 4px 6px; color: var(--efp-text); text-decoration: none; }
.z-brand__logo { width: 38px; height: 38px; object-fit: contain; }
.z-brand__text { display: grid; line-height: 1.15; }
.z-brand__text strong { font-size: 1rem; }
.z-brand__text small { font-size: .74rem; color: var(--efp-muted); }
.z-nav { display: grid; gap: 4px; }
.z-nav__item { display: flex; align-items: center; gap: 12px; padding: 10px 12px; border-radius: 10px; color: var(--efp-muted); text-decoration: none; font-size: .94rem; border: 1px solid transparent; }
.z-nav__item:hover { color: var(--efp-text); background: var(--efp-surface-muted); }
.z-nav__item--active { color: var(--efp-text); background: var(--z-active-nav); border-color: color-mix(in srgb, var(--efp-primary) 35%, transparent); }
.z-nav__item--active .v-icon { color: var(--efp-primary); }
.z-sidebar__footer { margin-top: auto; display: grid; gap: 12px; }
.z-level__label { display: block; margin: 0 4px 6px; font-size: .74rem; color: var(--efp-muted); }
.z-level__switch, .z-theme { display: flex; gap: 4px; padding: 3px; border: 1px solid var(--efp-border); border-radius: 12px; background: var(--efp-surface-muted); }
.z-level__switch button, .z-theme button { flex: 1; display: inline-flex; align-items: center; justify-content: center; gap: 6px; padding: 6px 8px; font-size: .8rem; color: var(--efp-muted); background: none; border: 0; border-radius: 9px; cursor: pointer; }
.z-level__switch button[aria-pressed='true'] { color: var(--efp-text); background: var(--z-active-nav); }
.z-level__switch button[aria-pressed='true'] .v-icon { color: var(--efp-primary); }
.z-theme__toggle { flex: 0 0 44px !important; }
.z-theme button:hover { color: var(--efp-text); }

.z-content { min-width: 0; display: flex; flex-direction: column; }
.z-header { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 16px 22px 4px; max-width: 1680px; width: 100%; margin: 0 auto; }
.z-header__title h1 { margin: 0; font-size: 1.7rem; font-weight: 650; line-height: 1.15; }
.z-header__title p { margin: 2px 0 0; color: var(--efp-muted); font-size: .9rem; }
.z-header__title :deep(.build-info) { margin-top: 2px; }
.z-header__meta { display: flex; align-items: center; gap: 14px; flex-wrap: wrap; justify-content: flex-end; }
.z-header__clock { font-size: .92rem; color: var(--efp-muted); white-space: nowrap; }
.z-header__clock strong { color: var(--efp-text); font-weight: 600; margin-left: 6px; font-variant-numeric: tabular-nums; }
.z-health { display: inline-flex; align-items: center; gap: 7px; font-size: .84rem; white-space: nowrap; color: inherit; background: none; border: 0; padding: 4px 6px; border-radius: 8px; cursor: pointer; }
.z-health:hover { background: var(--efp-surface-muted); }
.z-refresh { display: grid; gap: 8px; width: 280px; padding: 14px; color: var(--efp-text); background: var(--efp-surface); border: 1px solid var(--efp-border); border-radius: 12px; }
.z-refresh strong { font-size: .86rem; font-weight: 600; }
.z-refresh p { margin: 0; font-size: .76rem; color: var(--efp-muted); line-height: 1.4; }
.z-refresh__icon--connected { color: var(--efp-success); }
.z-refresh__icon--connecting { color: var(--efp-muted); }
.z-refresh__icon--disconnected { color: var(--efp-warning); }
.z-health i { width: 9px; height: 9px; border-radius: 50%; background: var(--efp-muted); }
.z-health--ok i { background: var(--efp-success); box-shadow: 0 0 0 3px color-mix(in srgb, var(--efp-success) 22%, transparent); }
.z-health--warning { color: var(--efp-warning-text); }
.z-health--warning i { background: var(--efp-warning); }
.z-expert-badge { padding: 2px 8px; font-size: .72rem; border-radius: 6px; border: 1px dashed var(--efp-border); color: var(--efp-muted); }
.z-user { display: grid; place-items: center; width: 38px; height: 38px; border-radius: 50%; color: var(--efp-text); background: var(--efp-surface); border: 1px solid var(--efp-border); cursor: pointer; }
.z-main { flex: 1; min-width: 0; }
.z-bottom-nav { display: none; }

/* Tablets: icon rail keeps the navigation one tap away without taking the room of the flow. */
@media (max-width: 1200px) {
  .z-shell { grid-template-columns: 76px minmax(0, 1fr); }
  .z-brand__text, .z-nav__item span, .z-level__label, .z-level__switch span, .z-theme__more span { display: none; }
  .z-brand { justify-content: center; }
  .z-nav__item { justify-content: center; padding: 12px 0; }
  .z-level__switch, .z-theme { flex-direction: column; }
  .z-theme__toggle { flex: 1 !important; }
}

/* Phones: no sidebar; a bottom bar within thumb reach. */
@media (max-width: 760px) {
  .z-shell { grid-template-columns: minmax(0, 1fr); }
  .z-sidebar { display: none; }
  .z-header { padding: 12px 12px 0; }
  .z-header__title h1 { font-size: 1.3rem; }
  .z-header__title p, .z-header__title :deep(.build-info), .z-header__clock, .z-expert-badge { display: none; }
  /* Room for the fixed bottom bar on every page (the car, heating and settings pages bring no padding of their own). */
  .z-main { padding-bottom: calc(76px + env(safe-area-inset-bottom)); }
  .z-bottom-nav { position: fixed; z-index: 1005; left: 0; right: 0; bottom: 0; display: flex; justify-content: space-around; padding: 6px 4px calc(6px + env(safe-area-inset-bottom));
    background: var(--efp-nav-background); border-top: 1px solid var(--efp-border); }
  .z-bottom-nav a { flex: 1; display: grid; justify-items: center; gap: 2px; padding: 4px 0; font-size: .68rem; color: var(--efp-muted); text-decoration: none; }
  .z-bottom-nav a.active { color: var(--efp-primary); }
}
</style>
