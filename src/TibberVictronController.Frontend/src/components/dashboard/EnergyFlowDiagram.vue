<script setup lang="ts">
import { computed } from 'vue';
import { formatNumber } from './dashboardFormatters';
import type { EnergyFlow, EnergyFlowNode, EnergyFlowNodeId } from './energyFlowDiagramTypes';

const props = defineProps<{
  nodes: EnergyFlowNode[];
  flows: EnergyFlow[];
  batterySoc: number | null;
}>();

const nodeMap = computed(() => new Map(props.nodes.map((node) => [node.id, node])));
const activeFlows = computed(() => props.flows
  .filter((flow) => flow.watts > 10)
  .map((flow) => ({
    ...flow,
    width: Math.max(4, Math.min(10, 3 + flow.watts / 900)),
    speed: Math.max(0.8, 2.3 - flow.watts / 4000),
    path: getPath(flow.from, flow.to),
    mobilePath: getMobilePath(flow.from, flow.to)
  })));
const totalLoadWatts = computed(() => props.flows
  .filter((flow) => flow.from === 'hub')
  .reduce((sum, flow) => sum + Math.max(0, flow.watts), 0));
const mainStateLabel = computed(() => {
  const strongestFlow = [...activeFlows.value].sort((left, right) => right.watts - left.watts)[0];

  return strongestFlow?.label ?? 'Idle';
});

function getPath(from: EnergyFlowNodeId, to: EnergyFlowNodeId): string {
  const paths: Partial<Record<`${EnergyFlowNodeId}-${EnergyFlowNodeId}`, string>> = {
    'pv-hub': 'M 292 120 C 405 120 455 198 510 238',
    'grid-hub': 'M 292 280 H 510',
    'battery-hub': 'M 292 440 C 405 440 455 362 510 322',
    'hub-grid': 'M 510 280 H 292',
    'hub-battery': 'M 510 322 C 455 362 405 440 292 440',
    'hub-house': 'M 690 280 C 775 280 820 280 908 280'
  };

  return paths[`${from}-${to}`] ?? '';
}

function getMobilePath(from: EnergyFlowNodeId, to: EnergyFlowNodeId): string {
  const paths: Partial<Record<`${EnergyFlowNodeId}-${EnergyFlowNodeId}`, string>> = {
    'pv-hub': 'M 76 112 C 76 174 138 220 176 270',
    'grid-hub': 'M 195 112 V 260',
    'battery-hub': 'M 314 112 C 314 174 254 220 214 270',
    'hub-grid': 'M 195 372 C 195 448 132 512 132 596',
    'hub-battery': 'M 195 372 C 195 448 258 512 258 596',
    'hub-house': 'M 195 372 V 494'
  };

  return paths[`${from}-${to}`] ?? '';
}

function formatKw(value: number): string {
  return `${formatNumber(value / 1000, 1)} kW`;
}
</script>

<template>
  <section class="energy-flow-diagram">
    <div class="energy-flow-diagram__header">
      <div>
        <span>Control Center</span>
        <h2>Energiefluss - Live</h2>
      </div>
      <v-chip size="small" variant="tonal" color="primary">{{ mainStateLabel }}</v-chip>
    </div>

    <div class="energy-flow-diagram__canvas energy-flow-diagram__canvas--desktop">
      <svg class="energy-flow-diagram__svg" viewBox="0 0 1200 560" aria-hidden="true">
        <path v-for="flow in flows" :key="`${flow.id}-base`" class="energy-flow-diagram__line-base" :d="getPath(flow.from, flow.to)" />
        <path
          v-for="flow in activeFlows"
          :key="flow.id"
          class="energy-flow-diagram__line-active"
          :class="`energy-flow-diagram__line-active--${flow.tone}`"
          :d="flow.path"
          :style="{ '--flow-width': `${flow.width}px`, '--flow-speed': `${flow.speed}s` }"
        />
      </svg>

      <article v-for="node in nodes.filter((item) => item.id !== 'hub')" :key="node.id" class="energy-flow-node" :class="[`energy-flow-node--${node.id}`, `energy-flow-node--${node.tone}`]">
        <v-icon :icon="node.icon" size="28" />
        <span>{{ node.label }}</span>
        <strong>{{ node.value }}</strong>
        <small>{{ node.subtitle }}</small>
      </article>

      <article class="energy-flow-hub">
        <v-icon icon="mdi-home-lightning-bolt-outline" size="34" />
        <span>Energy Hub</span>
        <strong>{{ formatKw(totalLoadWatts) }}</strong>
        <small>Verteilung im Haus</small>
      </article>
    </div>

    <div class="energy-flow-diagram__canvas energy-flow-diagram__canvas--mobile">
      <svg class="energy-flow-diagram__svg" viewBox="0 0 390 700" aria-hidden="true">
        <path v-for="flow in flows" :key="`${flow.id}-mobile-base`" class="energy-flow-diagram__line-base" :d="getMobilePath(flow.from, flow.to)" />
        <path
          v-for="flow in activeFlows"
          :key="`${flow.id}-mobile`"
          class="energy-flow-diagram__line-active"
          :class="`energy-flow-diagram__line-active--${flow.tone}`"
          :d="flow.mobilePath"
          :style="{ '--flow-width': `${Math.max(3, flow.width - 1)}px`, '--flow-speed': `${flow.speed}s` }"
        />
      </svg>

      <article v-if="nodeMap.get('pv')" class="energy-flow-mini-node energy-flow-mini-node--pv energy-flow-mini-node--pv-position">
        <v-icon :icon="nodeMap.get('pv')?.icon" size="22" />
        <span>PV</span>
        <strong>{{ nodeMap.get('pv')?.value }}</strong>
      </article>
      <article v-if="nodeMap.get('grid')" class="energy-flow-mini-node energy-flow-mini-node--grid energy-flow-mini-node--grid-position">
        <v-icon :icon="nodeMap.get('grid')?.icon" size="22" />
        <span>Netz</span>
        <strong>{{ nodeMap.get('grid')?.value }}</strong>
      </article>
      <article v-if="nodeMap.get('battery')" class="energy-flow-mini-node energy-flow-mini-node--battery energy-flow-mini-node--battery-position">
        <v-icon :icon="nodeMap.get('battery')?.icon" size="22" />
        <span>Akku</span>
        <strong>{{ batterySoc === null ? nodeMap.get('battery')?.value : `${formatNumber(batterySoc, 0)} %` }}</strong>
      </article>

      <article class="energy-flow-mobile-hub">
        <v-icon icon="mdi-home-lightning-bolt-outline" size="30" />
        <span>Energy Hub</span>
        <strong>{{ formatKw(totalLoadWatts) }}</strong>
      </article>

      <article v-if="nodeMap.get('house')" class="energy-flow-mini-node energy-flow-mini-node--load energy-flow-mini-node--house-position">
        <v-icon :icon="nodeMap.get('house')?.icon" size="22" />
        <span>Haus</span>
        <strong>{{ nodeMap.get('house')?.value }}</strong>
      </article>
      <article class="energy-flow-mini-node energy-flow-mini-node--grid energy-flow-mini-node--export-position">
        <v-icon icon="mdi-transmission-tower-export" size="22" />
        <span>Export</span>
        <strong>{{ formatKw(flows.find((flow) => flow.from === 'hub' && flow.to === 'grid')?.watts ?? 0) }}</strong>
      </article>
      <article class="energy-flow-mini-node energy-flow-mini-node--battery energy-flow-mini-node--charge-position">
        <v-icon icon="mdi-battery-charging-medium" size="22" />
        <span>Laden</span>
        <strong>{{ formatKw(flows.find((flow) => flow.from === 'hub' && flow.to === 'battery')?.watts ?? 0) }}</strong>
      </article>
    </div>

    <div class="energy-flow-diagram__chips">
      <v-chip v-for="flow in activeFlows" :key="`${flow.id}-chip`" size="small" variant="tonal" rounded="pill">
        <span>{{ flow.label }}</span>
        <strong>{{ formatKw(flow.watts) }}</strong>
      </v-chip>
    </div>
  </section>
</template>

<style scoped src="./EnergyFlowDiagram.css"></style>
