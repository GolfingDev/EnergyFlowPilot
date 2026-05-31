<script setup lang="ts">
import { computed, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useTheme } from 'vuetify';
import GeneralOverlay from './components/GeneralOverlay.vue'
import {
  defaultEnergyFlowTheme,
  energyFlowThemes,
  getEnergyFlowTheme,
  isEnergyFlowThemeName,
  type EnergyFlowThemeName
} from './themeRegistry';

type SettingsSectionKey = 'battery' | 'price' | 'forecast' | 'consumption' | 'decision' | 'system' | 'operations';

interface SettingsMenuGroup {
  title: string;
  items: Array<{
    label: string;
    section: SettingsSectionKey;
  }>;
}

const router = useRouter();
const route = useRoute();
const theme = useTheme();
const storedTheme = localStorage.getItem('energyFlowPilotTheme');
const isLoading = ref(false);
const isSettingsMenuOpen = ref(false);
const isThemeMenuOpen = ref(false);
const isSidebarThemeMenuOpen = ref(false);
const storedSidebarCollapsed = localStorage.getItem('energyFlowPilotExecutiveSidebarCollapsed');
const isExecutiveSidebarCollapsed = ref(storedSidebarCollapsed === 'true');

if (isEnergyFlowThemeName(storedTheme)) {
  theme.change(storedTheme);
} else {
  theme.change(defaultEnergyFlowTheme);
}

const settingsMenuGroups: SettingsMenuGroup[] = [
  {
    title: 'Batterie',
    items: [
      { label: 'Kapazität', section: 'battery' },
      { label: 'Ladegrenzen', section: 'battery' },
      { label: 'Planungsprofil', section: 'battery' }
    ]
  },
  {
    title: 'Forecast und Preise',
    items: [
      { label: 'Tibber', section: 'price' },
      { label: 'PV-Prognose', section: 'forecast' },
      { label: 'Verbrauch', section: 'consumption' }
    ]
  },
  {
    title: 'System',
    items: [
      { label: 'Decision Engine', section: 'decision' },
      { label: 'Victron Connection', section: 'system' },
      { label: 'Betrieb & Benachrichtigungen', section: 'operations' }
    ]
  }
];

const activeRouteName = computed(() => String(route.name ?? ''));
const activeTheme = computed(() => getEnergyFlowTheme(theme.global.name.value));
const usesExecutiveShell = computed(() => activeTheme.value.name === 'executiveDark');
const pageTitle = computed(() => activeRouteName.value === 'settings' ? 'Einstellungen' : 'Übersicht');

function navigateTo(routeName: string): void {
  void router.push({ name: routeName });
}

function navigateToDashboardSection(sectionId: string): void {
  void router.push({ name: 'dashboard' }).then(() => {
    window.setTimeout(() => {
      document.getElementById(sectionId)?.scrollIntoView({
        behavior: 'smooth',
        block: 'start'
      });
    }, 50);
  });
}

function openSettingsSection(section: SettingsSectionKey): void {
  isSettingsMenuOpen.value = false;
  void router.push({
    name: 'settings',
    query: { section }
  });
}

function changeTheme(nextTheme: EnergyFlowThemeName): void {
  isThemeMenuOpen.value = false;
  isSidebarThemeMenuOpen.value = false;
  theme.change(nextTheme);
  localStorage.setItem('energyFlowPilotTheme', nextTheme);
  window.dispatchEvent(new CustomEvent('energyflowpilot-theme-changed'));
}

function openSettingsPage(): void {
  openSettingsSection('battery');
}

function toggleExecutiveSidebar(): void {
  isExecutiveSidebarCollapsed.value = !isExecutiveSidebarCollapsed.value;
  localStorage.setItem('energyFlowPilotExecutiveSidebarCollapsed', String(isExecutiveSidebarCollapsed.value));
}
</script>

<template>
  <v-app
    class="app-shell"
    :class="{
      'app-shell--executive': usesExecutiveShell,
      'app-shell--executive-collapsed': usesExecutiveShell && isExecutiveSidebarCollapsed
    }"
  >
    <aside v-if="usesExecutiveShell" class="executive-sidebar" aria-label="Hauptnavigation">
      <button class="executive-sidebar__brand" type="button" aria-label="EnergyFlowPilot Startseite" @click="navigateTo('dashboard')">
        <img class="executive-sidebar__logo" src="/Logo.png" alt="" />
        <span>EnergyFlowPilot</span>
      </button>

      <nav class="executive-sidebar__nav">
        <button
          type="button"
          :class="{ 'executive-sidebar__item--active': activeRouteName === 'dashboard' }"
          @click="navigateTo('dashboard')"
        >
          <v-icon icon="mdi-home-outline" size="22" />
          <span>Übersicht</span>
        </button>
        <button type="button" @click="navigateToDashboardSection('dashboard-live-flow')">
          <v-icon icon="mdi-transmission-tower" size="22" />
          <span>Energiefluss</span>
        </button>
        <button type="button" @click="navigateToDashboardSection('dashboard-forecast')">
          <v-icon icon="mdi-chart-line" size="22" />
          <span>Prognosen</span>
        </button>
        <button type="button" @click="navigateToDashboardSection('dashboard-details')">
          <v-icon icon="mdi-alert-outline" size="22" />
          <span>Status</span>
        </button>
        <button
          type="button"
          :class="{ 'executive-sidebar__item--active': activeRouteName === 'settings' }"
          @click="openSettingsPage"
        >
          <v-icon icon="mdi-cog-outline" size="22" />
          <span>Einstellungen</span>
        </button>
      </nav>

      <button
        class="executive-sidebar__collapse"
        type="button"
        :aria-label="isExecutiveSidebarCollapsed ? 'Menü ausklappen' : 'Menü einklappen'"
        @click="toggleExecutiveSidebar"
      >
        <v-icon :icon="isExecutiveSidebarCollapsed ? 'mdi-chevron-right' : 'mdi-chevron-left'" size="20" />
        <span>Menü einklappen</span>
      </button>

      <div class="executive-sidebar__footer">
        <v-menu v-model="isSidebarThemeMenuOpen" location="end bottom" offset="12" :close-on-content-click="false">
          <template #activator="{ props }">
            <button class="executive-sidebar__theme" type="button" v-bind="props" :aria-label="`Design wechseln. Aktiv: ${activeTheme.label}`">
              <v-icon :icon="activeTheme.icon" size="20" />
              <span>
                <small>Design</small>
                <strong>{{ activeTheme.label }}</strong>
              </span>
            </button>
          </template>

          <div class="theme-menu">
            <div class="theme-menu__header">
              <strong>Design auswählen</strong>
              <span>Layout, Kontrast und Stimmung der Oberfläche.</span>
            </div>

            <button
              v-for="themeOption in energyFlowThemes"
              :key="themeOption.name"
              class="theme-menu__item"
              :class="{ 'theme-menu__item--active': activeTheme.name === themeOption.name }"
              type="button"
              @click="changeTheme(themeOption.name)"
            >
              <v-icon :icon="themeOption.icon" size="20" />
              <span>
                <strong>{{ themeOption.label }}</strong>
                <small>{{ themeOption.description }}</small>
              </span>
              <v-icon v-if="activeTheme.name === themeOption.name" icon="mdi-check" size="18" />
            </button>
          </div>
        </v-menu>
      </div>
    </aside>

    <v-app-bar class="top-bar" elevation="0" height="74">
      <div class="top-bar__inner">
        <h1 v-if="usesExecutiveShell" class="top-bar__title">{{ pageTitle }}</h1>

        <button class="top-bar__brand" type="button" aria-label="EnergyFlowPilot Startseite" @click="isLoading = true">
          <img class="top-bar__logo" src="/Logo.png" alt="" />
          <div class="top-bar__copy">
            <span class="top-bar__eyebrow">EnergyFlowPilot</span>
            <strong>Smart energy flow control for your home.</strong>
          </div>
        </button>

        <div class="top-bar__center">
          <nav class="top-nav" aria-label="Hauptnavigation">
            <button class="top-nav__item" :class="{ 'top-nav__item--active': activeRouteName === 'dashboard' }"
              @click="navigateTo('dashboard')">
              <span>Zusammenfassung</span>
              <small>Status, Forecast und Ersparnis</small>
            </button>

            <v-menu v-model="isSettingsMenuOpen" location="bottom center" offset="14" :close-on-content-click="false">
              <template #activator="{ props }">
                <button class="top-nav__item top-nav__item--menu"
                  :class="{ 'top-nav__item--active': activeRouteName === 'settings' || isSettingsMenuOpen }" v-bind="props">
                  <span>Einstellungen</span>
                  <small>Batterie, Preise, Prognosen und Sicherheit</small>
                </button>
              </template>

              <div class="mega-menu">
                <div class="mega-menu__header">
                  <div>
                    <strong>Controller-Konfiguration</strong>
                    <p>Alle Bereiche für Batterie, Forecast, Preise und Systemverhalten.</p>
                  </div>
                  <v-btn color="primary" variant="flat" @click="openSettingsPage">
                    Einstellungen öffnen
                  </v-btn>
                </div>

                <div class="mega-menu__grid">
                  <section v-for="group in settingsMenuGroups" :key="group.title" class="mega-menu__group">
                    <h3>{{ group.title }}</h3>
                    <ul>
                      <li v-for="item in group.items" :key="item.label">
                        <button class="mega-menu__link" type="button" @click="openSettingsSection(item.section)">
                          {{ item.label }}
                        </button>
                      </li>
                    </ul>
                  </section>
                </div>
              </div>
            </v-menu>
          </nav>
        </div>

        <div class="top-bar__actions">
          <v-menu v-model="isThemeMenuOpen" location="bottom end" offset="12" :close-on-content-click="false">
            <template #activator="{ props }">
              <button class="theme-picker-button" type="button" v-bind="props" :aria-label="`Theme wechseln. Aktiv: ${activeTheme.label}`">
                <v-icon :icon="activeTheme.icon" size="20" />
                <span>{{ activeTheme.label }}</span>
              </button>
            </template>

            <div class="theme-menu">
              <div class="theme-menu__header">
                <strong>Design auswählen</strong>
                <span>Farben, Kontrast und Stimmung der Oberfläche.</span>
              </div>

              <button
                v-for="themeOption in energyFlowThemes"
                :key="themeOption.name"
                class="theme-menu__item"
                :class="{ 'theme-menu__item--active': activeTheme.name === themeOption.name }"
                type="button"
                @click="changeTheme(themeOption.name)"
              >
                <v-icon :icon="themeOption.icon" size="20" />
                <span>
                  <strong>{{ themeOption.label }}</strong>
                  <small>{{ themeOption.description }}</small>
                </span>
                <v-icon v-if="activeTheme.name === themeOption.name" icon="mdi-check" size="18" />
              </button>
            </div>
          </v-menu>
        </div>
      </div>
    </v-app-bar>

    <v-main>
      <GeneralOverlay v-model="isLoading" image-src="Main.png" width="90vw" max-width="90vw" max-height="90vh" />
      <router-view />
    </v-main>
  </v-app>
</template>

<style scoped src="./App.css"></style>
