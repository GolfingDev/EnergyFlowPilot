<script setup lang="ts">
import { computed } from 'vue';
import { useTheme } from 'vuetify';
import type { CurrentBatteryDecisionResponseDto, DashboardTelemetryUpdateDto } from './dashboardTypes';
import { formatPercent, formatPower, getDecisionLabel } from './dashboardFormatters';
import { getEnergyFlowTheme } from '../../themeRegistry';

const props = defineProps<{
  decision: CurrentBatteryDecisionResponseDto | null;
  liveTelemetry: DashboardTelemetryUpdateDto | null;
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
const sceneImage = computed(() => getEnergyFlowTheme(theme.global.name.value).dark
  ? '/live-energy-scene-dark.png'
  : '/live-energy-scene-light.png');
const gridWatts = computed(() => props.liveTelemetry?.currentGridImportWatts ?? props.decision?.currentGridImportWatts ?? 0);
const gridImportWatts = computed(() => Math.max(0, gridWatts.value));
const targetPowerWatts = computed(() => Math.max(0, Math.abs(props.decision?.targetPowerWatts ?? 0)));
const homeWatts = computed(() => Math.max(0, props.liveTelemetry?.currentHouseConsumptionWatts ?? props.currentConsumptionWatts ?? 0));
const batteryWatts = computed(() => props.liveTelemetry?.currentBatteryPowerWatts ?? 0);
const batteryChargeWatts = computed(() => Math.max(0, batteryWatts.value));
const batteryDischargeWatts = computed(() => Math.max(0, -batteryWatts.value));
const pvWatts = computed(() => Math.max(0, homeWatts.value + batteryWatts.value - gridWatts.value));
const hasPvTelemetry = computed(() => pvWatts.value > 0);
const isCharging = computed(() => props.decision?.decisionState === 'Charge');
const chargeSource = computed(() => props.decision?.chargeSource?.toLowerCase() ?? '');
const isGridCharging = computed(() => isCharging.value && chargeSource.value.includes('grid'));
const isPvCharging = computed(() => isCharging.value && !isGridCharging.value);
const batterySoc = computed(() => props.liveTelemetry?.stateOfChargePercent ?? props.decision?.stateOfChargePercent ?? null);

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
      valueWatts: batteryDischargeWatts.value,
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
      valueWatts: isGridCharging.value ? Math.min(gridImportWatts.value, batteryChargeWatts.value) : 0,
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
        <strong>{{ formatPercent(batterySoc) }}</strong>
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
          <strong>{{ formatPower(pvWatts) }}</strong>
        </div>

        <div class="live-flow__node live-flow__node--battery">
          <v-icon icon="mdi-battery-charging-medium" size="22" />
          <span>Akku</span>
          <strong>{{ formatPower(batteryWatts) }}</strong>
        </div>

        <div class="live-flow__node live-flow__node--home">
          <v-icon icon="mdi-home-lightning-bolt-outline" size="23" />
          <span>Haus</span>
          <strong>{{ formatPower(currentConsumptionWatts) }}</strong>
        </div>

        <div class="live-flow__node live-flow__node--grid">
          <v-icon icon="mdi-transmission-tower" size="23" />
          <span>Netz</span>
          <strong>{{ formatPower(gridWatts) }}</strong>
        </div>
      </div>

      <aside class="live-flow__details">
        <div v-if="hasPvTelemetry" class="live-flow__metric">
          <span>PV-Produktion</span>
          <strong>{{ formatPower(pvWatts) }}</strong>
        </div>
        <div class="live-flow__metric">
          <span>Hausverbrauch</span>
          <strong>{{ formatPower(currentConsumptionWatts) }}</strong>
        </div>
        <div class="live-flow__metric">
          <span>Netzbezug</span>
          <strong>{{ formatPower(gridWatts) }}</strong>
        </div>
        <div class="live-flow__metric">
          <span>Akku-Leistung</span>
          <strong>{{ formatPower(batteryWatts) }}</strong>
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
