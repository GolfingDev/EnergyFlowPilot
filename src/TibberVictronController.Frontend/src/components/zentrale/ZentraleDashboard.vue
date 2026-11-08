<script setup lang="ts">
import { computed, ref } from 'vue';
import ZentraleAutomationsCard from './ZentraleAutomationsCard.vue';
import ZentraleDetailPanel from './ZentraleDetailPanel.vue';
import ZentraleEnergyFlow from './ZentraleEnergyFlow.vue';
import ZentraleForecastChart from './ZentraleForecastChart.vue';
import ZentraleKpiBar from './ZentraleKpiBar.vue';
import ZentralePlannedActions from './ZentralePlannedActions.vue';
import ZentraleStatusCard from './ZentraleStatusCard.vue';
import { useZentraleData } from './useZentraleData';
import SettingQuickFix from '../settings/SettingQuickFix.vue';
import type { ZentraleTarget } from './zentraleTypes';

/*
 * Start page of the Zentrale. Layout follows reading order: key figures, flow with status beside it,
 * forecast with planned actions below. Every element opens the same context panel, so control starts here.
 */
const data = useZentraleData();
// Missing settings of all sources, entered right on the dashboard (SettingQuickFix).
const missingSettings = computed(() => [...new Set(Object.values(data.state.missingSettings).flat())]);
const panelTarget = ref<ZentraleTarget | null>(null);
const panelOpen = ref(false);

function open(target: ZentraleTarget): void {
  panelTarget.value = target;
  panelOpen.value = true;
}
</script>

<template>
  <div class="z-page z-dashboard">
    <div v-if="Object.keys(data.state.errors).length" class="z-errors" role="alert">
      <v-icon icon="mdi-alert-outline" size="18" />
      <span v-for="(message, source) in data.state.errors" :key="source"><strong>{{ source }}:</strong> {{ message }}</span>
    </div>
    <SettingQuickFix v-if="missingSettings.length" :keys="missingSettings" @saved="data.refreshAll()" />

    <ZentraleKpiBar @open="open" />

    <div class="z-dashboard__main">
      <ZentraleEnergyFlow @open="open" />
      <div class="z-dashboard__side">
        <ZentraleStatusCard @open="open" />
        <ZentraleAutomationsCard @open="open" />
      </div>
    </div>

    <div class="z-dashboard__bottom">
      <ZentraleForecastChart />
      <ZentralePlannedActions @open="open" />
    </div>

    <ZentraleDetailPanel v-model="panelOpen" :target="panelTarget" />
  </div>
</template>

<style scoped>
.z-dashboard__main { display: grid; grid-template-columns: minmax(0, 1.75fr) minmax(300px, 1fr); gap: 14px; align-items: start; }
.z-dashboard__side { display: grid; gap: 14px; }
.z-dashboard__bottom { display: grid; grid-template-columns: minmax(0, 1.75fr) minmax(300px, 1fr); gap: 14px; align-items: start; }
.z-errors { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 16px; padding: 8px 12px; font-size: .82rem; color: var(--efp-warning-text);
  border: 1px solid color-mix(in srgb, var(--efp-warning) 45%, transparent); border-radius: 10px; background: color-mix(in srgb, var(--efp-warning) 8%, transparent); }
@media (max-width: 1180px) {
  .z-dashboard__main, .z-dashboard__bottom { grid-template-columns: minmax(0, 1fr); }
  .z-dashboard__side { grid-template-columns: repeat(2, minmax(0, 1fr)); }
}
@media (max-width: 760px) { .z-dashboard__side { grid-template-columns: minmax(0, 1fr); } }
</style>
