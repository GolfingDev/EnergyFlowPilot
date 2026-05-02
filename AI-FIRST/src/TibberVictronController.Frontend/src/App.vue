<script setup lang="ts">
import { computed } from 'vue';
import { useRoute, useRouter } from 'vue-router';

interface NavigationItem {
  title: string;
  subtitle: string;
  routeName: string;
}

const router = useRouter();
const route = useRoute();

const navigationItems: NavigationItem[] = [
  {
    title: 'Dashboard',
    subtitle: 'Status, Entscheidung, Forecast und Ersparnis',
    routeName: 'dashboard'
  },
  {
    title: 'Einstellungen',
    subtitle: 'Batterie, Preise, Prognosen und Sicherheit',
    routeName: 'settings'
  }
];

const activeRouteName = computed(() => String(route.name ?? ''));

function navigateTo(routeName: string): void {
  void router.push({ name: routeName });
}
</script>

<template>
  <v-app class="app-shell">
    <v-app-bar class="top-bar" elevation="0">
      <div class="top-bar__copy">
        <span class="top-bar__eyebrow">EnergyFlowPilot</span>
        <strong>Smart energy flow control for your home.</strong>
      </div>
    </v-app-bar>

    <v-navigation-drawer
      class="side-rail"
      color="transparent"
      elevation="0"
      permanent
      width="280"
    >
          <nav class="nav-list" aria-label="Hauptnavigation">
        <button
          v-for="item in navigationItems"
          :key="item.routeName"
          class="nav-item"
          :class="{ 'nav-item--active': activeRouteName === item.routeName }"
          @click="navigateTo(item.routeName)"
        >
          <span>{{ item.title }}</span>
          <small>{{ item.subtitle }}</small>
        </button>
      </nav>
    </v-navigation-drawer>

    <v-main>
      <router-view />
    </v-main>
  </v-app>
</template>

<style scoped src="./App.css"></style>
