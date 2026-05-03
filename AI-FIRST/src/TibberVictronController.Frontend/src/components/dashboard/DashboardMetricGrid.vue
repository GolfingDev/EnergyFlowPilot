<script setup lang="ts">
import { computed } from 'vue';
import type {
  BatteryForecastResponseDto,
  BatterySavingsResponseDto,
  CurrentBatteryDecisionResponseDto,
  SavingsPeriod,
  SavingsPeriodOption
} from './dashboardTypes';
import { formatCurrency, formatPercent, formatPower, formatPrice, getDecisionLabel } from './dashboardFormatters';

const props = defineProps<{
  decision: CurrentBatteryDecisionResponseDto | null;
  forecast: BatteryForecastResponseDto | null;
  savings: BatterySavingsResponseDto | null;
  savingsCurrency: string;
  savingsPeriod: SavingsPeriod;
  savingsPeriodOptions: readonly SavingsPeriodOption[];
  currentConsumptionWatts: number | null;
}>();

defineEmits<{
  changeSavingsPeriod: [period: SavingsPeriod];
}>();

const decisionLabel = computed(() => props.decision
  ? getDecisionLabel(props.decision.decisionState, props.decision.chargeSource)
  : 'Nicht verfuegbar');

const savingsMetricTitle = computed(() => {
  const label = props.savingsPeriodOptions.find((option) => option.value === props.savingsPeriod)?.label ?? 'Tag';

  return `Ersparnis ${label}`;
});
</script>

<template>
  <section class="metric-grid">
    <article class="metric-card">
      <span>Akku-SoC</span>
      <strong>{{ formatPercent(decision?.stateOfChargePercent ?? forecast?.initialStateOfChargePercent) }}</strong>
      <p>Aktueller oder zuletzt berechneter Startwert.</p>
    </article>

    <article class="metric-card">
      <span>Aktuelle Entscheidung</span>
      <strong>{{ decisionLabel }}</strong>
      <p>{{ formatPower(decision?.targetPowerWatts) }}</p>
    </article>

    <article class="metric-card">
      <span>Tibber Preis</span>
      <strong>{{ formatPrice(decision?.tibberPricePerKwh, decision?.tibberPriceCurrency) }}</strong>
      <p>Preis aus der aktuellen Entscheidung.</p>
    </article>

    <article class="metric-card">
      <div class="metric-card__header">
        <span>{{ savingsMetricTitle }}</span>
        <div class="metric-card__toggle" aria-label="Ersparnis-Zeitraum">
          <button
            v-for="period in savingsPeriodOptions"
            :key="period.value"
            :class="{ 'metric-card__toggle-button--active': savingsPeriod === period.value }"
            type="button"
            @click="$emit('changeSavingsPeriod', period.value)"
          >
            {{ period.label }}
          </button>
        </div>
      </div>
      <strong>{{ savings ? formatCurrency(savings.aggregate.netSavings, savingsCurrency) : 'Nicht verfuegbar' }}</strong>
      <p>Netto-Ersparnis im gewaehlten Zeitraum.</p>
      <p>Aktueller Verbrauch: <b>{{ formatPower(currentConsumptionWatts) }}</b></p>
      <p>Netzleistung: <b>{{ formatPower(decision?.currentGridImportWatts) }}</b></p>
    </article>
  </section>
</template>

<style scoped src="./DashboardMetricGrid.css"></style>
