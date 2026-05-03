<script setup lang="ts">
import { computed, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useTheme } from 'vuetify';
import GeneralOverlay from './components/GeneralOverlay.vue'

type SettingsSectionKey = 'battery' | 'price' | 'forecast' | 'consumption' | 'decision' | 'system';

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

if (storedTheme === 'controllerDark' || storedTheme === 'controllerLight') {
  theme.change(storedTheme);
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
      { label: 'Benachrichtigungen', section: 'system' }
    ]
  }
];

const activeRouteName = computed(() => String(route.name ?? ''));
const isDarkTheme = computed(() => theme.global.name.value === 'controllerDark');
const nextTheme = ref(isDarkTheme.value ? 'controllerLight' : 'controllerDark');

function navigateTo(routeName: string): void {
  void router.push({ name: routeName });
}

function openSettingsSection(section: SettingsSectionKey): void {
  isSettingsMenuOpen.value = false;
  void router.push({
    name: 'settings',
    query: { section }
  });
}

function toggleTheme(): void {
  theme.change(nextTheme.value);
  localStorage.setItem('energyFlowPilotTheme', nextTheme.value);
  window.dispatchEvent(new CustomEvent('energyflowpilot-theme-changed'));
}

function openSettingsPage(): void {
  openSettingsSection('battery');
}
</script>

<template>
  <v-app class="app-shell">
    <v-app-bar class="top-bar" elevation="0" height="74">
      <div class="top-bar__inner">
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
          <v-switch v-model="nextTheme" class="theme-toggle" prepend-icon="mdi-white-balance-sunny"
            append-icon="mdi-weather-night" false-value="controllerDark" true-value="controllerLight"
            hide-details density="compact" @click="toggleTheme"></v-switch>
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
