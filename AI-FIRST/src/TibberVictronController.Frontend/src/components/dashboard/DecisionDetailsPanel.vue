<script setup lang="ts">
import { computed } from 'vue';
import type { CurrentBatteryDecisionResponseDto } from './dashboardTypes';
import { formatDateTime, formatPower, getDecisionLabel } from './dashboardFormatters';

const props = defineProps<{
  decision: CurrentBatteryDecisionResponseDto | null;
}>();

const decisionLabel = computed(() => props.decision
  ? getDecisionLabel(props.decision.decisionState, props.decision.chargeSource)
  : 'Nicht verfuegbar');
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

    <p v-else class="empty-state">Noch keine aktuelle Entscheidung verfuegbar.</p>
  </section>
</template>

<style scoped src="./DecisionDetailsPanel.css"></style>
