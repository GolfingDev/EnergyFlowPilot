<script setup lang="ts">
import type { BatterySavingsResponseDto } from './dashboardTypes';
import { formatNumber } from './dashboardFormatters';

defineProps<{
  savings: BatterySavingsResponseDto | null;
}>();
</script>

<template>
  <section class="panel">
    <div class="panel__header">
      <div>
        <h2>Energie heute</h2>
        <p>Gespeicherte Ersparnis- und Batteriewerte.</p>
      </div>
    </div>

    <div v-if="savings" class="status-list">
      <div class="status-row">
        <span>Aus Netz geladen</span>
        <strong>{{ formatNumber(savings.aggregate.gridChargedEnergyKwh, 2) }} kWh</strong>
      </div>
      <div class="status-row">
        <span>Aus PV geladen</span>
        <strong>{{ formatNumber(savings.aggregate.pvChargedEnergyKwh, 2) }} kWh</strong>
      </div>
      <div class="status-row">
        <span>Entladen</span>
        <strong>{{ formatNumber(savings.aggregate.dischargedEnergyKwh, 2) }} kWh</strong>
      </div>
    </div>

    <p v-else class="empty-state">Noch keine Ersparnisdaten verfuegbar.</p>
  </section>
</template>

<style scoped src="./EnergySavingsPanel.css"></style>
