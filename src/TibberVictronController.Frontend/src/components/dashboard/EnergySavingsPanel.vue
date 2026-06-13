<script setup lang="ts">
import { computed } from 'vue';
import type { BatterySavingsResponseDto, SavingsPeriod, SavingsPeriodOption } from './dashboardTypes';
import { formatNumber } from './dashboardFormatters';

const props = defineProps<{
  savings: BatterySavingsResponseDto | null;
  period?: SavingsPeriod;
  periodOptions?: readonly SavingsPeriodOption[];
}>();

const emit = defineEmits<{
  changePeriod: [period: SavingsPeriod];
}>();

const energyMax = computed(() => Math.max(
  props.savings?.aggregate.pvChargedEnergyKwh ?? 0,
  props.savings?.aggregate.gridChargedEnergyKwh ?? 0,
  props.savings?.aggregate.dischargedEnergyKwh ?? 0,
  0.001
));

function barPct(value: number): string {
  return `${Math.round((value / energyMax.value) * 100)}%`;
}
</script>

<template>
  <section class="panel">
    <div class="panel__header">
      <div>
        <h2>Energiebilanz</h2>
        <p>Geladene und entladene Energie nach Zeitraum.</p>
      </div>
      <div v-if="periodOptions" class="savings-period-tabs" role="group" aria-label="Zeitraum">
        <button
          v-for="opt in periodOptions"
          :key="opt.value"
          type="button"
          :class="{ 'savings-period-tabs__btn--active': period === opt.value }"
          @click="emit('changePeriod', opt.value)"
        >{{ opt.label }}</button>
      </div>
    </div>

    <div v-if="savings" class="energy-bars">
      <div class="energy-bar-row">
        <span class="energy-bar-row__label">Aus PV geladen</span>
        <div class="energy-bar-row__track">
          <div class="energy-bar-row__fill energy-bar-row__fill--pv"
               :style="{ width: barPct(savings.aggregate.pvChargedEnergyKwh) }" />
        </div>
        <span class="energy-bar-row__val">{{ formatNumber(savings.aggregate.pvChargedEnergyKwh, 2) }} kWh</span>
      </div>
      <div class="energy-bar-row">
        <span class="energy-bar-row__label">Aus Netz geladen</span>
        <div class="energy-bar-row__track">
          <div class="energy-bar-row__fill energy-bar-row__fill--grid"
               :style="{ width: barPct(savings.aggregate.gridChargedEnergyKwh) }" />
        </div>
        <span class="energy-bar-row__val">{{ formatNumber(savings.aggregate.gridChargedEnergyKwh, 2) }} kWh</span>
      </div>
      <div class="energy-bar-row">
        <span class="energy-bar-row__label">Entladen</span>
        <div class="energy-bar-row__track">
          <div class="energy-bar-row__fill energy-bar-row__fill--discharge"
               :style="{ width: barPct(savings.aggregate.dischargedEnergyKwh) }" />
        </div>
        <span class="energy-bar-row__val">{{ formatNumber(savings.aggregate.dischargedEnergyKwh, 2) }} kWh</span>
      </div>
    </div>

    <p v-else class="empty-state">Noch keine Ersparnisdaten verfuegbar.</p>
  </section>
</template>

<style scoped src="./EnergySavingsPanel.css"></style>
