<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import EnergySavingsPanel from '../components/dashboard/EnergySavingsPanel.vue';
import WeishauptEnergyPanel from '../components/heating/WeishauptEnergyPanel.vue';
import DynamicTariffComparison from '../components/settings/DynamicTariffComparison.vue';
import ZentraleReviewPanel from '../components/zentrale/ZentraleReviewPanel.vue';
import { errorText, getJson } from '../components/zentrale/zentraleApi';
import type { BatterySavingsResponseDto, SavingsPeriod, SavingsPeriodOption } from '../components/dashboard/dashboardTypes';
import '../components/zentrale/zentrale.css';

/** Analysis gathers the existing evaluations in one place, one topic per tab instead of one long page. */
type Tab = 'costs' | 'review' | 'heatpump' | 'tariff';
const tabs: { key: Tab; label: string; icon: string }[] = [
  { key: 'costs', label: 'Kosten & Einsparungen', icon: 'mdi-piggy-bank-outline' },
  { key: 'review', label: 'Netz & Speicher', icon: 'mdi-history' },
  { key: 'heatpump', label: 'Wärmepumpe', icon: 'mdi-heat-pump-outline' },
  { key: 'tariff', label: 'Tarifvergleich', icon: 'mdi-scale-balance' }
];
const route = useRoute();
const router = useRouter();
const tab = computed<Tab>(() => tabs.some(t => t.key === route.query.tab) ? route.query.tab as Tab : 'costs');

const periodOptions: readonly SavingsPeriodOption[] = [
  { label: 'Heute', value: 'day' }, { label: 'Gestern', value: 'yesterday' }, { label: 'Woche', value: 'week' }, { label: 'Monat', value: 'month' }, { label: 'Jahr', value: 'year' }
];
const period = ref<SavingsPeriod>('day');
const savings = ref<BatterySavingsResponseDto | null>(null);
const savingsLoading = ref(false);
const savingsError = ref<string | null>(null);

// Same request as the classic dashboard: "yesterday" is the day period with yesterday as reference date.
async function loadSavings(): Promise<void> {
  savingsLoading.value = true;
  try {
    const today = new Date();
    const reference = period.value === 'yesterday' ? new Date(today.getFullYear(), today.getMonth(), today.getDate() - 1) : today;
    const parameters = new URLSearchParams({ period: period.value === 'yesterday' ? 'day' : period.value, referenceDate: reference.toISOString().slice(0, 10), currency: 'EUR' });
    savings.value = await getJson<BatterySavingsResponseDto>(`/api/savings?${parameters}`, 'Ersparnis konnte nicht geladen werden');
    savingsError.value = null;
  } catch (reason) {
    savings.value = null;
    savingsError.value = errorText(reason, 'Ersparnis konnte nicht geladen werden.');
  } finally {
    savingsLoading.value = false;
  }
}

function selectTab(key: Tab): void {
  void router.replace({ query: { ...route.query, tab: key } });
}

watch([tab, period], () => { if (tab.value === 'costs') void loadSavings(); }, { immediate: true });
</script>

<template>
  <div class="z-page">
    <div class="z-tabs" role="tablist" aria-label="Analysebereiche">
      <button v-for="item in tabs" :key="item.key" type="button" role="tab" :aria-selected="tab === item.key" @click="selectTab(item.key)">
        <v-icon :icon="item.icon" size="18" />{{ item.label }}
      </button>
      <router-link class="z-link z-analysis__heating" to="/heating">Raumklima & Heizkreise <v-icon icon="mdi-arrow-right" size="16" /></router-link>
    </div>

    <template v-if="tab === 'costs'">
      <p v-if="savingsError" class="z-note z-note--error" role="alert">{{ savingsError }}</p>
      <EnergySavingsPanel :savings="savings" :period="period" :period-options="periodOptions" :loading="savingsLoading" @change-period="period = $event" />
      <p class="z-page__intro">Einsparung der Akkus: vermiedene Netzkosten durch Entladung abzüglich Kosten der Ladung. Modellrechnung aus Messwerten und Intervallpreisen, keine Stromabrechnung.</p>
    </template>
    <ZentraleReviewPanel v-else-if="tab === 'review'" />
    <WeishauptEnergyPanel v-else-if="tab === 'heatpump'" />
    <DynamicTariffComparison v-else-if="tab === 'tariff'" />
  </div>
</template>

<style scoped>
.z-analysis__heating { margin-left: auto; }
</style>
