<script setup lang="ts">
import { computed } from 'vue';
import type { CurrentBatteryDecisionResponseDto } from './dashboardTypes';
import { formatPercent, formatPower } from './dashboardFormatters';

const props = defineProps<{
  decision: CurrentBatteryDecisionResponseDto | null;
  currentConsumptionWatts: number | null;
}>();

const hasPvTelemetry = computed(() => (props.decision?.currentPvProductionWatts ?? 0) > 0);
const pvWatts = computed(() => hasPvTelemetry.value ? Math.max(0, props.decision?.currentPvProductionWatts ?? 0) : null);
const gridPowerWatts = computed(() => props.decision?.currentGridImportWatts ?? null);
const batteryPowerWatts = computed(() => {
  if (!props.decision) {
    return null;
  }

  if (props.decision.decisionState === 'Charge') {
    return props.decision.targetPowerWatts;
  }

  if (props.decision.decisionState === 'Discharge') {
    return -props.decision.targetPowerWatts;
  }

  return 0;
});
const batteryLabel = computed(() => {
  if (!props.decision) {
    return 'Nicht verfügbar';
  }

  const power = formatPower(batteryPowerWatts.value);
  const soc = formatPercent(props.decision.stateOfChargePercent);

  return `${soc} · ${power}`;
});
const isCharging = computed(() => (batteryPowerWatts.value ?? 0) > 0);
const isDischarging = computed(() => (batteryPowerWatts.value ?? 0) < 0);
const hasGridImport = computed(() => (gridPowerWatts.value ?? 0) > 0);
const hasGridExport = computed(() => (gridPowerWatts.value ?? 0) < 0);
</script>

<template>
  <section class="panel schematic-flow">
    <div class="schematic-flow__header">
      <div>
        <span>Control Center</span>
        <h2>Energiefluss - Live</h2>
      </div>
    </div>

    <div class="schematic-flow__stage">
      <svg class="schematic-flow__lines" viewBox="0 0 900 420" aria-hidden="true">
        <path
          class="schematic-flow__line schematic-flow__line--generation"
          :class="{ 'schematic-flow__line--active': hasPvTelemetry }"
          d="M 280 78 H 450"
        />
        <path
          class="schematic-flow__line schematic-flow__line--grid"
          :class="{ 'schematic-flow__line--active': hasGridImport || hasGridExport }"
          d="M 280 314 H 450"
        />
        <path
          class="schematic-flow__line"
          :class="{
            'schematic-flow__line--charge': isCharging,
            'schematic-flow__line--discharge': isDischarging,
            'schematic-flow__line--battery-idle': !isCharging && !isDischarging,
            'schematic-flow__line--active': isCharging || isDischarging
          }"
          d="M 450 78 H 620"
        />
        <path
          class="schematic-flow__line schematic-flow__line--consumption"
          :class="{ 'schematic-flow__line--active': currentConsumptionWatts !== null }"
          d="M 450 314 H 620"
        />
      </svg>

      <article class="schematic-flow__node schematic-flow__node--pv">
        <v-icon icon="mdi-solar-power-variant-outline" size="30" />
        <span>PV-Anlage</span>
        <strong>{{ pvWatts === null ? 'Keine Daten' : formatPower(pvWatts) }}</strong>
      </article>

      <article class="schematic-flow__node schematic-flow__node--grid">
        <v-icon icon="mdi-transmission-tower" size="30" />
        <span>Netz</span>
        <strong>{{ formatPower(gridPowerWatts) }}</strong>
      </article>

      <div class="schematic-flow__home">
        <v-icon icon="mdi-home-outline" size="44" />
      </div>

      <article class="schematic-flow__node schematic-flow__node--battery">
        <v-icon icon="mdi-battery-charging-medium" size="30" />
        <span>Batteriespeicher</span>
        <strong>{{ batteryLabel }}</strong>
      </article>

      <article class="schematic-flow__node schematic-flow__node--home">
        <v-icon icon="mdi-home-lightning-bolt-outline" size="30" />
        <span>Hausverbrauch</span>
        <strong>{{ formatPower(currentConsumptionWatts) }}</strong>
      </article>

    </div>

    <div class="schematic-flow__legend">
      <span><i class="schematic-flow__legend-line schematic-flow__legend-line--generation"></i> Erzeugung</span>
      <span><i class="schematic-flow__legend-line schematic-flow__legend-line--consumption"></i> Verbrauch</span>
      <span><i class="schematic-flow__legend-line schematic-flow__legend-line--charge"></i> Laden</span>
      <span><i class="schematic-flow__legend-line schematic-flow__legend-line--discharge"></i> Entladen</span>
      <span><i class="schematic-flow__legend-line schematic-flow__legend-line--grid"></i> Netzfluss</span>
    </div>
  </section>
</template>

<style scoped src="./SchematicEnergyFlowPanel.css"></style>
