<script setup lang="ts">
import { computed } from 'vue';
import { useTheme } from 'vuetify';
import type { CurrentBatteryDecisionResponseDto } from './dashboardTypes';
import { formatPercent, formatPower, getDecisionLabel } from './dashboardFormatters';

const props = defineProps<{
  decision: CurrentBatteryDecisionResponseDto | null;
  currentConsumptionWatts: number | null;
}>();

const theme = useTheme();

interface FlowDefinition {
  key: string;
  label: string;
  valueWatts: number;
  path: string;
  color: 'cyan' | 'green';
}

const decisionLabel = computed(() => props.decision
  ? getDecisionLabel(props.decision.decisionState, props.decision.chargeSource)
  : 'Nicht verfuegbar');
const sceneImage = computed(() => theme.global.name.value === 'controllerDark'
  ? '/live-energy-scene-dark.png'
  : '/live-energy-scene-light.png');
const hasPvTelemetry = computed(() => false);
const pvWatts = computed(() => hasPvTelemetry.value ? Math.max(0, props.decision?.currentPvProductionWatts ?? 0) : 0);
const gridImportWatts = computed(() => Math.max(0, props.decision?.currentGridImportWatts ?? 0));
const targetPowerWatts = computed(() => Math.max(0, Math.abs(props.decision?.targetPowerWatts ?? 0)));
const homeWatts = computed(() => Math.max(0, props.currentConsumptionWatts ?? 0));
const isCharging = computed(() => props.decision?.decisionState === 'Charge');
const isDischarging = computed(() => props.decision?.decisionState === 'Discharge');
const chargeSource = computed(() => props.decision?.chargeSource?.toLowerCase() ?? '');
const isGridCharging = computed(() => isCharging.value && chargeSource.value.includes('grid'));
const isPvCharging = computed(() => isCharging.value && !isGridCharging.value);

const flows = computed<FlowDefinition[]>(() => {
  const pvToBatteryWatts = isPvCharging.value ? Math.min(pvWatts.value, targetPowerWatts.value) : 0;
  const pvToHomeWatts = Math.max(0, Math.min(pvWatts.value - pvToBatteryWatts, homeWatts.value));

  const values: FlowDefinition[] = [];

  if (hasPvTelemetry.value) {
    values.push({
      key: 'pv-home',
      label: 'PV -> Haus',
      valueWatts: pvToHomeWatts,
      path: 'M 710 300 C 735 390 790 515 865 600',
      color: 'cyan'
    });

    values.push({
      key: 'pv-battery',
      label: 'PV -> Akku',
      valueWatts: pvToBatteryWatts,
      path: 'M 680 320 C 590 420 455 520 255 610',
      color: 'green'
    });
  }

  values.push(
    {
      key: 'battery-home',
      label: 'Akku -> Haus',
      valueWatts: isDischarging.value ? targetPowerWatts.value : 0,
      path: 'M 255 620 C 440 655 680 645 865 612',
      color: 'green'
    },
    {
      key: 'grid-home',
      label: 'Netz -> Haus',
      valueWatts: gridImportWatts.value,
      path: 'M 1345 500 C 1185 545 1010 575 900 610',
      color: 'cyan'
    },
    {
      key: 'grid-battery',
      label: 'Netz -> Akku',
      valueWatts: isGridCharging.value ? targetPowerWatts.value : 0,
      path: 'M 1340 525 C 1040 705 620 710 260 635',
      color: 'green'
    }
  );

  return values;
});

const activeFlows = computed(() => flows.value.filter((flow) => flow.valueWatts > 0));

function getFlowWidth(valueWatts: number): number {
  return valueWatts > 0
    ? Math.min(10, Math.max(3, valueWatts / 420))
    : 0;
}
</script>

<template>
  <section class="panel live-flow">
    <div class="live-flow__header">
      <div>
        <span class="live-flow__eyebrow">Live Energy Flow</span>
        <h2>Aktueller Energiefluss</h2>
      </div>
      <div class="live-flow__state">
        <span>{{ decisionLabel }}</span>
        <strong>{{ formatPercent(decision?.stateOfChargePercent) }}</strong>
      </div>
    </div>

    <div class="live-flow__body">
      <div class="live-flow__stage">
        <img class="live-flow__scene" :src="sceneImage" alt="" aria-hidden="true">

        <svg class="live-flow__overlay" viewBox="0 0 1600 900" aria-hidden="true">
          <path
            v-for="flow in flows"
            :key="flow.key"
            class="live-flow__path"
            :class="[
              `live-flow__path--${flow.color}`,
              flow.valueWatts > 0 ? 'live-flow__path--active' : 'live-flow__path--idle'
            ]"
            :d="flow.path"
            :style="{ '--flow-width': `${getFlowWidth(flow.valueWatts)}px` }"
            pathLength="100"
          />
        </svg>

        <div v-if="hasPvTelemetry" class="live-flow__node live-flow__node--pv">
          <v-icon icon="mdi-solar-power-variant" size="22" />
          <span>PV</span>
          <strong>{{ formatPower(decision?.currentPvProductionWatts) }}</strong>
        </div>

        <div class="live-flow__node live-flow__node--battery">
          <v-icon icon="mdi-battery-charging-medium" size="22" />
          <span>Akku</span>
          <strong>{{ formatPower(targetPowerWatts) }}</strong>
        </div>

        <div class="live-flow__node live-flow__node--home">
          <v-icon icon="mdi-home-lightning-bolt-outline" size="23" />
          <span>Haus</span>
          <strong>{{ formatPower(currentConsumptionWatts) }}</strong>
        </div>

        <div class="live-flow__node live-flow__node--grid">
          <v-icon icon="mdi-transmission-tower" size="23" />
          <span>Netz</span>
          <strong>{{ formatPower(decision?.currentGridImportWatts) }}</strong>
        </div>
      </div>

      <aside class="live-flow__details">
        <div v-if="hasPvTelemetry" class="live-flow__metric">
          <span>PV-Produktion</span>
          <strong>{{ formatPower(decision?.currentPvProductionWatts) }}</strong>
        </div>
        <div class="live-flow__metric">
          <span>Hausverbrauch</span>
          <strong>{{ formatPower(currentConsumptionWatts) }}</strong>
        </div>
        <div class="live-flow__metric">
          <span>Netzbezug</span>
          <strong>{{ formatPower(decision?.currentGridImportWatts) }}</strong>
        </div>
        <div class="live-flow__metric">
          <span>Akku-Leistung</span>
          <strong>{{ formatPower(targetPowerWatts) }}</strong>
        </div>

        <div class="live-flow__routes">
          <span>Aktive Wege</span>
          <p v-if="activeFlows.length === 0">Keine aktive Leistungsrichtung erkannt.</p>
          <p v-for="flow in activeFlows" :key="flow.key">
            <b>{{ flow.label }}</b>
            {{ formatPower(flow.valueWatts) }}
          </p>
        </div>
      </aside>
    </div>
  </section>
</template>

<style scoped src="./LiveEnergyFlowPanel.css"></style>
