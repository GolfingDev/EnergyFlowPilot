<script setup lang="ts">
import { computed } from 'vue';
import type { ControllerStatusResponseDto } from './dashboardTypes';
import { formatDateTime } from './dashboardFormatters';

const props = defineProps<{
  status: ControllerStatusResponseDto | null;
  isLoading: boolean;
  autoRefreshLabel: string;
}>();

defineEmits<{
  refresh: [];
}>();

const healthItems = computed(() => [
  {
    label: 'Controller',
    value: props.status?.status ?? 'Unbekannt',
    color: getStatusColor(props.status?.status)
  },
  {
    label: 'Victron MQTT',
    value: props.status?.victronMqttStatus ?? 'Unbekannt',
    color: getStatusColor(props.status?.victronMqttStatus)
  },
  {
    label: 'Letzte Daten',
    value: props.status?.victronMqttLastSuccessfulMessageAtUtc
      ? formatDateTime(props.status.victronMqttLastSuccessfulMessageAtUtc)
      : 'Keine Daten',
    color: props.status?.victronMqttLastSuccessfulMessageAtUtc ? 'primary' : 'warning'
  }
]);

function getStatusColor(value: string | null | undefined): string {
  if (!value) {
    return 'warning';
  }

  const normalizedValue = value.toLowerCase();

  if (normalizedValue.includes('healthy') || normalizedValue.includes('connected') || normalizedValue.includes('running')) {
    return 'success';
  }

  if (normalizedValue.includes('error') || normalizedValue.includes('failed') || normalizedValue.includes('stale')) {
    return 'error';
  }

  return 'warning';
}
</script>

<template>
  <header class="dashboard-header">
    <div class="dashboard-header__status">
      <div class="status-chips" aria-label="Systemstatus">
        <v-chip v-for="item in healthItems" :key="item.label" :color="item.color" size="small" variant="tonal">
          {{ item.label }}: {{ item.value }}
        </v-chip>
      </div>

      <div>
        <span class="dashboard-header__eyebrow">EnergyFlowPilot</span>
        <h1>Dashboard</h1>
        <p>Live-Ueberblick fuer Batterie, Steuerentscheidung, Forecast und Ersparnis.</p>
      </div>

      <div class="dashboard-header__actions">
        <v-btn variant="outlined" :loading="isLoading" @click="$emit('refresh')">
          Aktualisieren
        </v-btn>
        <span class="dashboard-header__refresh">{{ autoRefreshLabel }}</span>
      </div>
    </div>
  </header>
</template>

<style scoped src="./DashboardHeader.css"></style>
