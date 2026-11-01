<script setup lang="ts">
import CarDataPanel from '../components/dashboard/CarDataPanel.vue';
import EvChargerPanel from '../components/dashboard/EvChargerPanel.vue';
</script>

<template>
  <v-container class="ev-page" fluid>
    <header class="ev-page__header">
      <span class="ev-page__eyebrow">Charge Amps Halo · BMW i4</span>
      <h1>Auto</h1>
      <p>Wallbox-Verbindung, Ladevorgänge und später Ladeplanung für die nächsten Fahrten.</p>
    </header>
    <CarDataPanel />
    <EvChargerPanel />
  </v-container>
</template>

<style scoped>
.ev-page {
  display: flex;
  flex-direction: column;
  gap: 20px;
  max-width: 1200px;
}

.ev-page__eyebrow {
  color: var(--efp-muted);
  font-size: 0.74rem;
  font-weight: 700;
  text-transform: uppercase;
}

.ev-page__header h1 {
  margin: 6px 0;
  color: var(--efp-text);
  font-size: 2rem;
  line-height: 1.1;
}

.ev-page__header p {
  max-width: 640px;
  margin: 0;
  color: var(--efp-muted);
  line-height: 1.5;
}
</style>
