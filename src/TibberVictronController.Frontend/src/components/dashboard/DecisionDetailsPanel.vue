<script setup lang="ts">
import { computed } from 'vue';
import type { CurrentBatteryDecisionResponseDto, DecisionLogEntryResponseDto } from './dashboardTypes';
import { formatDateTime, formatPercent, formatPower, formatPrice, getDecisionLabel } from './dashboardFormatters';

const props = defineProps<{
  decision: CurrentBatteryDecisionResponseDto | null;
  decisionLogEntries: DecisionLogEntryResponseDto[];
}>();

const decisionLabel = computed(() => props.decision
  ? getDecisionLabel(props.decision.decisionState, props.decision.chargeSource)
  : 'Nicht verfuegbar');

function getLogDecisionLabel(entry: DecisionLogEntryResponseDto): string {
  return getDecisionLabel(entry.decisionState, entry.chargeSource);
}
</script>

<template>
  <section class="panel decision-panel">
    <div class="panel__header">
      <div>
        <h2>Aktuelle Steuerentscheidung</h2>
        <p>Nachvollziehbare Entscheidung der Decision Engine.</p>
      </div>
      <span v-if="decision">{{ formatDateTime(decision.decidedAtUtc) }}</span>
    </div>

    <div v-if="decision" class="decision-summary">
      <div>
        <span>Status</span>
        <strong>{{ decisionLabel }}</strong>
      </div>
      <div>
        <span>Netzbezug</span>
        <strong>{{ formatPower(decision.currentGridImportWatts) }}</strong>
      </div>
      <div>
        <span>PV-Leistung</span>
        <strong>{{ formatPower(decision.currentPvProductionWatts) }}</strong>
      </div>
    </div>

    <div v-if="decision?.reasons.length" class="reason-list">
      <div v-for="reason in decision.reasons" :key="`${reason.ruleId}-${reason.message}`" class="reason-row">
        <strong>{{ reason.ruleId }}</strong>
        <span>{{ reason.message }}</span>
      </div>
    </div>

    <div v-if="decisionLogEntries.length" class="decision-log">
      <div class="decision-log__header">
        <h3>Letzte Entscheidungen</h3>
        <span>{{ decisionLogEntries.length }} Einträge</span>
      </div>

      <v-expansion-panels variant="accordion">
        <v-expansion-panel
          v-for="entry in decisionLogEntries"
          :key="entry.id"
          :title="`${formatDateTime(entry.decidedAtUtc)} · ${getLogDecisionLabel(entry)}`"
          :text="entry.reasons[0]?.message ?? 'Keine Begründung vorhanden.'"
        >
          <template #text>
            <div class="decision-log-entry">
              <div class="decision-log-entry__summary">
                <span>SoC: <b>{{ formatPercent(entry.stateOfChargePercent) }}</b></span>
                <span>Leistung: <b>{{ formatPower(entry.targetPowerWatts) }}</b></span>
                <span>Preis: <b>{{ formatPrice(entry.tibberPricePerKwh, entry.tibberPriceCurrency) }}</b></span>
                <span>Netzbezug: <b>{{ formatPower(entry.gridImportWatts) }}</b></span>
                <span>Netzexport: <b>{{ formatPower(entry.gridExportWatts) }}</b></span>
              </div>

              <div class="reason-list decision-log-entry__reasons">
                <div v-for="reason in entry.reasons" :key="`${entry.id}-${reason.ruleId}-${reason.message}`" class="reason-row">
                  <strong>{{ reason.ruleId }}</strong>
                  <span>{{ reason.message }}</span>
                </div>
              </div>
            </div>
          </template>
        </v-expansion-panel>
      </v-expansion-panels>
    </div>

    <p v-else class="empty-state">Noch keine aktuelle Entscheidung verfuegbar.</p>
  </section>
</template>

<style scoped src="./DecisionDetailsPanel.css"></style>
