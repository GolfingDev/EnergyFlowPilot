<script setup lang="ts">
import { computed } from 'vue';
import EnergyFlowDiagram from './EnergyFlowDiagram.vue';
import type { CurrentBatteryDecisionResponseDto } from './dashboardTypes';
import type { EnergyFlow, EnergyFlowNode } from './energyFlowDiagramTypes';
import { formatPercent, formatPower } from './dashboardFormatters';

const props = defineProps<{
  decision: CurrentBatteryDecisionResponseDto | null;
  currentConsumptionWatts: number | null;
}>();

const pvWatts = computed(() => Math.max(0, props.decision?.currentPvProductionWatts ?? 0));
const gridWatts = computed(() => props.decision?.currentGridImportWatts ?? 0);
const houseWatts = computed(() => Math.max(0, props.currentConsumptionWatts ?? 0));
const batteryWatts = computed(() => {
  if (!props.decision) {
    return 0;
  }

  if (props.decision.decisionState === 'Charge') {
    return props.decision.targetPowerWatts;
  }

  if (props.decision.decisionState === 'Discharge') {
    return -props.decision.targetPowerWatts;
  }

  return 0;
});
const batterySoc = computed(() => props.decision?.stateOfChargePercent ?? null);
const batterySubtitle = computed(() => {
  if (batteryWatts.value > 0) {
    return `Lädt mit ${formatPower(batteryWatts.value)}`;
  }

  if (batteryWatts.value < 0) {
    return `Entlädt mit ${formatPower(Math.abs(batteryWatts.value))}`;
  }

  return 'Standby';
});
const gridSubtitle = computed(() => {
  if (gridWatts.value > 0) {
    return 'Netzbezug';
  }

  if (gridWatts.value < 0) {
    return 'Einspeisung';
  }

  return 'Kein Fluss';
});
const nodes = computed<EnergyFlowNode[]>(() => [
  {
    id: 'pv',
    label: 'PV-Anlage',
    value: pvWatts.value > 0 ? formatPower(pvWatts.value) : 'Keine Daten',
    subtitle: 'Erzeugung',
    icon: 'mdi-solar-power-variant-outline',
    tone: 'pv'
  },
  {
    id: 'grid',
    label: 'Netz',
    value: formatPower(Math.abs(gridWatts.value)),
    subtitle: gridSubtitle.value,
    icon: gridWatts.value < 0 ? 'mdi-transmission-tower-export' : 'mdi-transmission-tower-import',
    tone: 'grid'
  },
  {
    id: 'battery',
    label: 'Batteriespeicher',
    value: batterySoc.value === null ? 'Nicht verfügbar' : formatPercent(batterySoc.value),
    subtitle: batterySubtitle.value,
    icon: batteryWatts.value > 0 ? 'mdi-battery-charging-medium' : 'mdi-battery-medium',
    tone: batteryWatts.value < 0 ? 'batteryDischarge' : 'batteryCharge'
  },
  {
    id: 'hub',
    label: 'Energy Hub',
    value: formatPower(houseWatts.value),
    subtitle: 'Verteilung im Haus',
    icon: 'mdi-home-lightning-bolt-outline',
    tone: 'load'
  },
  {
    id: 'house',
    label: 'Hausverbrauch',
    value: formatPower(houseWatts.value),
    subtitle: 'Aktueller Bedarf',
    icon: 'mdi-home-lightning-bolt-outline',
    tone: 'load'
  }
]);
const flows = computed<EnergyFlow[]>(() => [
  {
    id: 'pv-to-hub',
    from: 'pv',
    to: 'hub',
    label: 'PV -> Hub',
    watts: pvWatts.value,
    tone: 'pv'
  },
  {
    id: 'grid-to-hub',
    from: 'grid',
    to: 'hub',
    label: 'Netz -> Hub',
    watts: Math.max(0, gridWatts.value),
    tone: 'grid'
  },
  {
    id: 'hub-to-grid',
    from: 'hub',
    to: 'grid',
    label: 'Hub -> Netz',
    watts: Math.max(0, -gridWatts.value),
    tone: 'grid'
  },
  {
    id: 'battery-to-hub',
    from: 'battery',
    to: 'hub',
    label: 'Akku -> Hub',
    watts: Math.max(0, -batteryWatts.value),
    tone: 'batteryDischarge'
  },
  {
    id: 'hub-to-battery',
    from: 'hub',
    to: 'battery',
    label: 'Hub -> Akku',
    watts: Math.max(0, batteryWatts.value),
    tone: 'batteryCharge'
  },
  {
    id: 'hub-to-house',
    from: 'hub',
    to: 'house',
    label: 'Hub -> Haus',
    watts: houseWatts.value,
    tone: 'load'
  }
]);
</script>

<template>
  <section class="panel schematic-flow">
    <EnergyFlowDiagram :nodes="nodes" :flows="flows" :battery-soc="batterySoc" />
  </section>
</template>

<style scoped src="./SchematicEnergyFlowPanel.css"></style>
