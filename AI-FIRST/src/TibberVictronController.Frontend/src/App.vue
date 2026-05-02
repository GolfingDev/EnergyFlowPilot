<script setup lang="ts">
import { computed } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useTheme } from 'vuetify';

interface NavigationItem {
  title: string;
  subtitle: string;
  routeName: string;
}

const router = useRouter();
const route = useRoute();
const theme = useTheme();
const storedTheme = localStorage.getItem('energyFlowPilotTheme');

if (storedTheme === 'controllerDark' || storedTheme === 'controllerLight') {
  theme.change(storedTheme);
}

const navigationItems: NavigationItem[] = [
  {
    title: 'Zusammenfassung',
    subtitle: 'Status, Forecast und Ersparnis',
    routeName: 'dashboard'
  },
  {
    title: 'Einstellungen',
    subtitle: 'Batterie, Preise, Prognosen und Sicherheit',
    routeName: 'settings'
  }
];

const activeRouteName = computed(() => String(route.name ?? ''));
const isDarkTheme = computed(() => theme.global.name.value === 'controllerDark');

function navigateTo(routeName: string): void {
  void router.push({ name: routeName });
}

function toggleTheme(): void {
  const nextTheme = isDarkTheme.value ? 'controllerLight' : 'controllerDark';
  theme.global.name.value = nextTheme;
  localStorage.setItem('energyFlowPilotTheme', nextTheme);
  window.dispatchEvent(new CustomEvent('energyflowpilot-theme-changed'));
}
</script>

<template>
  <v-app class="app-shell">
    <v-app-bar class="top-bar" elevation="0">
      <div class="top-bar__brand">
        <img class="top-bar__logo" src="/Main.png" alt="EnergyFlowPilot" />
        <div class="top-bar__copy">
          <span class="top-bar__eyebrow">EnergyFlowPilot</span>
          <strong>Smart energy flow control for your home.</strong>
        </div>
      </div>

      <nav class="top-nav" aria-label="Hauptnavigation">
        <button v-for="item in navigationItems" :key="item.routeName" class="top-nav__item"
          :class="{ 'top-nav__item--active': activeRouteName === item.routeName }" @click="navigateTo(item.routeName)">
          <span>{{ item.title }}</span>
          <small>{{ item.subtitle }}</small>
        </button>
      </nav>

      <v-spacer />

      <v-switch class="theme-toggle" prepend-icon="mdi-white-balance-sunny" append-icon="mdi-weather-night"
        false-value="Helles Design" true-value="Dunkles Design" @click="toggleTheme"></v-switch>
      <!-- {{ isDarkTheme ? 'Helles Design' : 'Dunkles Design' }} -->

    </v-app-bar>

    <v-main>
      <router-view />
    </v-main>
  </v-app>
</template>

<style scoped src="./App.css"></style>
