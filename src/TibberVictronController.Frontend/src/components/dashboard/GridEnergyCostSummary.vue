<script setup lang="ts">
import type { DashboardCostComparisonDto } from '../leitstand/leitstandTypes';

withDefaults(defineProps<{
  costs: DashboardCostComparisonDto | null;
  loading?: boolean;
  error?: string | null;
}>(), { loading: false, error: null });

// Use the API's interval-priced total, rather than multiplying energy by an average price.
const money = new Intl.NumberFormat('de-DE', { style: 'currency', currency: 'EUR' });
const percent = new Intl.NumberFormat('de-DE', { maximumFractionDigits: 1 });
</script>

<template>
  <div class="grid-cost" role="status" :aria-busy="loading">
    <span class="grid-cost-label">{{ costs && costs.pricedCoveragePercent < 100 ? 'Erfasste Netzkosten im Zeitraum' : 'Gesamte Netzkosten im Zeitraum' }}</span>
    <strong class="grid-cost-value">{{ loading ? 'Wird berechnet …' : error || !costs ? '–' : money.format(costs.actualCost) }}</strong>
    <span v-if="!loading && !error && costs" class="grid-cost-note">
      Netzbezug abzüglich Einspeisevergütung · ohne Grundpreis.<br>
      Aus erfassten Messwerten und den jeweiligen Intervallpreisen berechnet.
      <template v-if="costs.pricedCoveragePercent < 100">
        Unvollständig: Nur {{ percent.format(costs.pricedCoveragePercent) }} % der erfassten Messintervalle konnten bewertet werden.
      </template>
    </span>
    <span v-else-if="!loading" class="grid-cost-note">{{ error ? 'Kosten konnten nicht geladen werden.' : 'Für diesen Zeitraum fehlen Messwerte oder Preise zur Kostenberechnung.' }}</span>
  </div>
</template>

<style scoped>
.grid-cost { display: grid; gap: 4px; padding: 12px 0; border-top: 1px solid var(--efp-border-soft, #dfe7e9); }
.grid-cost-label { font-size: 12px; font-weight: 600; }
.grid-cost-value { font-size: 24px; font-weight: 650; font-variant-numeric: tabular-nums; }
.grid-cost-note { font-size: 11px; line-height: 1.6; color: var(--efp-muted, #65757d); }
</style>
