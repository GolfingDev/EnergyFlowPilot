<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { expertMode } from '../uiMode';
import ZentraleAutomationsCard from '../components/zentrale/ZentraleAutomationsCard.vue';
import ZentraleDetailPanel from '../components/zentrale/ZentraleDetailPanel.vue';
import ZentralePlannedActions from '../components/zentrale/ZentralePlannedActions.vue';
import { errorText, getJson } from '../components/zentrale/zentraleApi';
import { kw, percent, priceText, when } from '../components/zentrale/zentraleFormat';
import { getDecisionLabel } from '../components/dashboard/dashboardFormatters';
import { useZentraleData } from '../components/zentrale/useZentraleData';
import type { DecisionLogEntryResponseDto } from '../components/dashboard/dashboardTypes';
import type { ZentraleTarget } from '../components/zentrale/zentraleTypes';
import '../components/zentrale/zentrale.css';

/** Which rules act, why, what comes next, and the decision history that proves it. */
useZentraleData();
const panelTarget = ref<ZentraleTarget | null>(null);
const panelOpen = ref(false);
const entries = ref<DecisionLogEntryResponseDto[]>([]);
const error = ref<string | null>(null);
const filter = ref<'all' | 'Charge' | 'Discharge' | 'Idle'>('all');
const expandedId = ref<string | null>(null);

/** Inputs and thresholds of one decision as short facts; only values the backend logged appear. */
function inputFacts(entry: DecisionLogEntryResponseDto): string[] {
  const i = entry.inputs;
  if (!i) return [];
  const facts: (string | null)[] = [
    i.gridImportWatts != null ? `Netz ${i.gridImportWatts < 0 ? 'Einspeisung' : 'Bezug'} ${kw(i.gridImportWatts)}` : null,
    i.pvProductionWatts != null ? `PV ${kw(i.pvProductionWatts)}` : null,
    i.batteryPowerWatts != null ? `Akku ${i.batteryPowerWatts > 0 ? 'lädt' : i.batteryPowerWatts < 0 ? 'entlädt' : 'ruht'} ${kw(i.batteryPowerWatts)}` : null,
    i.minimumSavingsPerKwh != null ? `Mindestvorteil ${priceText(i.minimumSavingsPerKwh)}` : null,
    i.wearCostPerKwh ? `Verschleiß ${priceText(i.wearCostPerKwh)}` : null,
    i.planningMinimumStateOfChargePercent != null ? `Reserve ${percent(i.planningMinimumStateOfChargePercent)}` : null,
    i.planningMaximumStateOfChargePercent != null ? `Ladegrenze ${percent(i.planningMaximumStateOfChargePercent)}` : null,
    i.maximumChargePowerWatts != null ? `max. Laden ${kw(i.maximumChargePowerWatts)}` : null,
    i.maximumDischargePowerWatts != null ? `max. Entladen ${kw(i.maximumDischargePowerWatts)}` : null,
    expertMode.value && i.roundTripEfficiencyPercent != null ? `Wirkungsgrad ${percent(i.roundTripEfficiencyPercent)}` : null,
    expertMode.value && i.electricityPriceProvider ? `Preisquelle ${i.electricityPriceProvider}` : null
  ];
  return facts.filter((f): f is string => f !== null);
}

function toggle(entry: DecisionLogEntryResponseDto): void {
  expandedId.value = expandedId.value === entry.id ? null : entry.id;
}
let timer: number | undefined;

const filtered = computed(() => filter.value === 'all' ? entries.value : entries.value.filter(e => e.decisionState === filter.value));

function open(target: ZentraleTarget): void {
  panelTarget.value = target;
  panelOpen.value = true;
}

async function load(): Promise<void> {
  try {
    entries.value = await getJson<DecisionLogEntryResponseDto[]>('/api/decision/logs?maxCount=60', 'Entscheidungsverlauf konnte nicht geladen werden');
    error.value = null;
  } catch (reason) {
    error.value = errorText(reason, 'Entscheidungsverlauf konnte nicht geladen werden.');
  }
}

onMounted(() => { void load(); timer = window.setInterval(() => void load(), 60_000); });
onBeforeUnmount(() => window.clearInterval(timer));
</script>

<template>
  <div class="z-page">
    <div class="z-grid-2">
      <ZentraleAutomationsCard detailed @open="open" />
      <ZentralePlannedActions :limit="10" @open="open" />
    </div>

    <section class="z-card" aria-labelledby="z-history-title">
      <header class="z-card__head">
        <h2 id="z-history-title"><v-icon icon="mdi-timeline-text-outline" size="22" class="z-history__icon" />Entscheidungsverlauf Akku-Optimierung</h2>
        <div class="z-tabs" role="tablist" aria-label="Nach Entscheidung filtern">
          <button type="button" role="tab" :aria-selected="filter === 'all'" @click="filter = 'all'">Alle</button>
          <button type="button" role="tab" :aria-selected="filter === 'Charge'" @click="filter = 'Charge'">Laden</button>
          <button type="button" role="tab" :aria-selected="filter === 'Discharge'" @click="filter = 'Discharge'">Entladen</button>
          <button type="button" role="tab" :aria-selected="filter === 'Idle'" @click="filter = 'Idle'">Ruhe</button>
        </div>
      </header>
      <p v-if="error" class="z-note z-note--error" role="alert">{{ error }}</p>
      <div class="z-history">
        <table>
          <thead>
            <tr><th>Zeit</th><th>Entscheidung</th><th>Sollwert</th><th>Preis</th><th>Akku 1</th><th>Begründung</th><th v-if="expertMode">Regel</th></tr>
          </thead>
          <tbody>
            <template v-for="entry in filtered" :key="entry.id">
            <tr class="z-history__row" :aria-expanded="expandedId === entry.id" tabindex="0" @click="toggle(entry)" @keydown.enter="toggle(entry)">
              <td class="z-history__time"><v-icon :icon="expandedId === entry.id ? 'mdi-chevron-down' : 'mdi-chevron-right'" size="16" />{{ when(entry.decidedAtUtc) }}</td>
              <td><span :class="`z-decision z-decision--${entry.decisionState.toLowerCase()}`">{{ getDecisionLabel(entry.decisionState, entry.chargeSource) }}</span></td>
              <td>{{ entry.targetPowerWatts ? kw(entry.targetPowerWatts) : '–' }}</td>
              <td>{{ priceText(entry.tibberPricePerKwh, entry.tibberPriceCurrency) }}</td>
              <td>{{ percent(entry.stateOfChargePercent) }}</td>
              <td class="z-history__reason">{{ entry.reasons[0]?.message ?? '–' }}</td>
              <td v-if="expertMode"><code>{{ entry.reasons[0]?.ruleId ?? '–' }}</code></td>
            </tr>
            <tr v-if="expandedId === entry.id" class="z-history__details">
              <td :colspan="expertMode ? 7 : 6">
                <p v-if="entry.reasons.length > 1" class="z-note">Weitere Gründe: {{ entry.reasons.slice(1).map(r => r.message).join(' · ') }}</p>
                <p v-if="inputFacts(entry).length" class="z-history__facts"><span v-for="fact in inputFacts(entry)" :key="fact">{{ fact }}</span></p>
                <p v-else class="z-note">Für diesen Eintrag wurden noch keine Eingaben und Schwellen protokolliert.</p>
                <p class="z-note">Schwellen sind die Einstellungen zum Entscheidungszeitpunkt; Vergleichspreise innerhalb der Regeln werden nicht protokolliert.</p>
              </td>
            </tr>
            </template>
            <tr v-if="!filtered.length"><td :colspan="expertMode ? 7 : 6" class="z-note">Keine Einträge.</td></tr>
          </tbody>
        </table>
      </div>
    </section>

    <ZentraleDetailPanel v-model="panelOpen" :target="panelTarget" />
  </div>
</template>

<style scoped>
.z-history__icon { color: var(--efp-primary); }
.z-history { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; font-size: .83rem; }
th { text-align: left; font-weight: 600; font-size: .74rem; color: var(--efp-muted); text-transform: uppercase; letter-spacing: .04em; padding: 6px 8px; border-bottom: 1px solid var(--efp-border); white-space: nowrap; }
td { padding: 7px 8px; border-bottom: 1px solid var(--efp-border-soft); vertical-align: top; font-variant-numeric: tabular-nums; }
.z-history__time { white-space: nowrap; color: var(--efp-secondary); }
.z-history__reason { min-width: 260px; line-height: 1.4; }
.z-history__row { cursor: pointer; }
.z-history__row:hover td { background: var(--efp-surface-muted); }
.z-history__details td { background: var(--efp-surface-muted); }
.z-history__facts { display: flex; flex-wrap: wrap; gap: 6px; margin: 4px 0; }
.z-history__facts span { padding: 2px 8px; font-size: .76rem; border: 1px solid var(--efp-border); border-radius: 99px; }
.z-decision { padding: 1px 8px; border-radius: 99px; font-size: .76rem; white-space: nowrap; border: 1px solid var(--efp-border); }
.z-decision--charge { color: var(--z-battery); border-color: color-mix(in srgb, var(--z-battery) 45%, transparent); }
.z-decision--discharge { color: var(--z-house); border-color: color-mix(in srgb, var(--z-house) 45%, transparent); }
</style>
