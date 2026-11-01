<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';

interface PvForecastCorrectionDto {
  enabled: boolean;
  isReady: boolean;
  daysWithData: number;
  requiredDays: number;
  overallFactor: number;
  hours: { localHour: number; factor: number }[];
  calculatedAtUtc: string | null;
}

const correction = ref<PvForecastCorrectionDto | null>(null);
const error = ref<string | null>(null);
const percent = (factor: number) => `${Math.round(factor * 100)} %`;
const deviation = computed(() => {
  const factor = correction.value?.overallFactor ?? 1;
  if (Math.abs(factor - 1) < 0.03) return 'Die Prognose trifft im Mittel gut.';
  return factor < 1
    ? `Die Prognose liegt im Mittel ${Math.round((1 - factor) * 100)} % zu hoch.`
    : `Die Prognose liegt im Mittel ${Math.round((factor - 1) * 100)} % zu niedrig.`;
});

onMounted(async () => {
  try {
    const response = await fetch('/api/forecast/pv-correction');
    if (!response.ok) throw new Error(`PV-Korrektur konnte nicht geladen werden (HTTP ${response.status}).`);
    correction.value = await response.json() as PvForecastCorrectionDto;
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : 'PV-Korrektur konnte nicht geladen werden.';
  }
});
</script>

<template>
  <v-card variant="outlined" class="pa-4 mb-4" aria-label="Gelernte PV-Korrektur">
    <h3 class="text-subtitle-1 mb-2">Gelernte PV-Korrektur</h3>
    <v-alert v-if="error" type="warning" variant="tonal" density="compact">{{ error }}</v-alert>
    <template v-else-if="correction">
      <p v-if="correction.daysWithData === 0">Noch keine Daten: Der Controller vergleicht ab jetzt Prognose und Messung je Viertelstunde.</p>
      <template v-else>
        <p>
          Gemessen gegen Prognose, letzte 14 Tage ({{ correction.daysWithData }} Tage mit PV): gesamt <strong>{{ percent(correction.overallFactor) }}</strong>.
          {{ deviation }}
        </p>
        <p class="pv-correction__meta">
          {{ correction.isReady
            ? (correction.enabled ? 'Wird in der Planung angewendet.' : 'Bereit; in der Planung erst nach dem Einschalten oben („Gelernte PV-Korrektur anwenden“).')
            : `Noch nicht verlässlich: ab ${correction.requiredDays} Tagen Daten wirksam.` }}
        </p>
        <div v-if="correction.hours.length" class="pv-correction__hours mt-2">
          <span v-for="hour in correction.hours" :key="hour.localHour" class="pv-correction__hour"
            :class="{ 'pv-correction__hour--low': hour.factor < 0.9, 'pv-correction__hour--high': hour.factor > 1.1 }">
            {{ hour.localHour }} Uhr: {{ percent(hour.factor) }}
          </span>
        </div>
      </template>
    </template>
  </v-card>
</template>

<style scoped>
.pv-correction__meta {
  font-size: 0.85rem;
  opacity: 0.75;
}

.pv-correction__hours {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.pv-correction__hour {
  font-size: 0.8rem;
  padding: 2px 8px;
  border-radius: 10px;
  border: 1px solid var(--efp-border-soft, #e8eef4);
}

.pv-correction__hour--low {
  border-color: #d97706;
}

.pv-correction__hour--high {
  border-color: #16a34a;
}
</style>
