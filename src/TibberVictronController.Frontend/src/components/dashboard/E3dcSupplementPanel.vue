<script setup lang="ts">
import type { E3dcTelemetryResponseDto } from './dashboardTypes';
import { formatDateTime, formatPower, formatPercent } from './dashboardFormatters';

const props = defineProps<{
  telemetry: E3dcTelemetryResponseDto;
}>();

function gridLabel(watts: number): string {
  if (watts > 0) return 'Netzbezug';
  if (watts < 0) return 'Einspeisung';
  return 'Ausgeglichen';
}

function batteryLabel(watts: number | null): string {
  if (watts === null) return '';
  if (watts > 0) return 'Laden';
  if (watts < 0) return 'Entladen';
  return 'Standby';
}

function formatAbsPower(watts: number): string {
  return formatPower(Math.abs(watts));
}
</script>

<template>
  <div class="e3dc-metrics">
    <div v-if="telemetry.pvProductionWatts !== null" class="e3dc-metric">
      <span class="e3dc-metric__label">PV</span>
      <span class="e3dc-metric__value e3dc-metric__value--pv">{{ formatPower(telemetry.pvProductionWatts) }}</span>
    </div>

    <div v-if="telemetry.gridImportWatts !== null" class="e3dc-metric">
      <span class="e3dc-metric__label">{{ gridLabel(telemetry.gridImportWatts) }}</span>
      <span class="e3dc-metric__value">{{ formatAbsPower(telemetry.gridImportWatts) }}</span>
    </div>

    <div v-if="telemetry.batteryPowerWatts !== null" class="e3dc-metric">
      <span class="e3dc-metric__label">Batterie ({{ batteryLabel(telemetry.batteryPowerWatts) }})</span>
      <span
        class="e3dc-metric__value"
        :class="telemetry.batteryPowerWatts < 0 ? 'e3dc-metric__value--discharging' : 'e3dc-metric__value--charging'"
      >{{ formatAbsPower(telemetry.batteryPowerWatts) }}</span>
    </div>

    <div v-if="telemetry.batterySocPercent !== null" class="e3dc-metric">
      <span class="e3dc-metric__label">SoC</span>
      <span class="e3dc-metric__value">{{ formatPercent(telemetry.batterySocPercent) }}</span>
    </div>

    <div v-if="telemetry.lastSuccessfulPollAtUtc" class="e3dc-timestamp">
      Zuletzt: {{ formatDateTime(telemetry.lastSuccessfulPollAtUtc) }}
    </div>
  </div>
</template>

<style scoped>
.e3dc-metrics {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding-top: 12px;
}

.e3dc-metric {
  display: flex;
  justify-content: space-between;
  align-items: baseline;
  gap: 8px;
}

.e3dc-metric__label {
  font-size: 0.8rem;
  opacity: 0.65;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.e3dc-metric__value {
  font-size: 1rem;
  font-weight: 600;
}

.e3dc-metric__value--pv {
  color: #F9A825;
}

.e3dc-metric__value--discharging {
  color: #26C6DA;
}

.e3dc-metric__value--charging {
  color: #66BB6A;
}

.e3dc-timestamp {
  margin-top: 4px;
  font-size: 0.7rem;
  opacity: 0.45;
}
</style>
