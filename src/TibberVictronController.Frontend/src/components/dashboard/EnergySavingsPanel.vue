<script setup lang="ts">
import { computed } from 'vue';
import type { BatterySavingsResponseDto, SavingsPeriod, SavingsPeriodOption } from './dashboardTypes';
import { formatNumber } from './dashboardFormatters';

const props = defineProps<{
  savings: BatterySavingsResponseDto | null;
  period?: SavingsPeriod;
  periodOptions?: readonly SavingsPeriodOption[];
  loading?: boolean;
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

    <div :class="['energy-bars', { 'energy-bars--loading': loading }]">
      <template v-if="loading || savings">
        <div class="energy-bar-row">
          <span class="energy-bar-row__label">Aus PV geladen</span>
          <div class="energy-bar-row__track">
            <div class="energy-bar-row__fill energy-bar-row__fill--pv"
                 :style="{ width: savings ? barPct(savings.aggregate.pvChargedEnergyKwh) : '0%' }" />
          </div>
          <span class="energy-bar-row__val">{{ savings ? `${formatNumber(savings.aggregate.pvChargedEnergyKwh, 2)} kWh` : '–' }}</span>
        </div>
        <div class="energy-bar-row">
          <span class="energy-bar-row__label">Aus Netz geladen</span>
          <div class="energy-bar-row__track">
            <div class="energy-bar-row__fill energy-bar-row__fill--grid"
                 :style="{ width: savings ? barPct(savings.aggregate.gridChargedEnergyKwh) : '0%' }" />
          </div>
          <span class="energy-bar-row__val">{{ savings ? `${formatNumber(savings.aggregate.gridChargedEnergyKwh, 2)} kWh` : '–' }}</span>
        </div>
        <div class="energy-bar-row">
          <span class="energy-bar-row__label">Entladen</span>
          <div class="energy-bar-row__track">
            <div class="energy-bar-row__fill energy-bar-row__fill--discharge"
                 :style="{ width: savings ? barPct(savings.aggregate.dischargedEnergyKwh) : '0%' }" />
          </div>
          <span class="energy-bar-row__val">{{ savings ? `${formatNumber(savings.aggregate.dischargedEnergyKwh, 2)} kWh` : '–' }}</span>
        </div>
        <div v-if="loading" class="energy-bars__spinner" aria-label="Lade Daten…">
          <span class="energy-bars__dot" />
          <span class="energy-bars__dot" />
          <span class="energy-bars__dot" />
        </div>
      </template>
      <p v-else class="empty-state">Noch keine Ersparnisdaten verfügbar.</p>
    </div>
  </section>
</template>

<style scoped src="./EnergySavingsPanel.css"></style>
