<script setup lang="ts">
import { computed, ref } from 'vue';
import { formatDateTime, formatNumber, formatPercent, formatPower, formatPrice } from './dashboardFormatters';
import type { JointBatteryShadowDto } from './dashboardTypes';

// canControl: false for viewers; status and plan stay visible, only the operator actions are hidden.
const props = withDefaults(defineProps<{ state: JointBatteryShadowDto | null; recoveryBusy?: boolean; manualChargeBusy?: boolean; canControl?: boolean }>(),
  { recoveryBusy: false, manualChargeBusy: false, canControl: true });
const emit = defineEmits<{
  confirmRecovery: [];
  startManualCharge: [targetKwh: number, maximumPowerKw: number | null];
  stopManualCharge: [];
}>();

const manualTargetKwh = ref<number | string>(2);
const manualMaximumKw = ref<number | string>('');

const needsAttention = computed(() => !!props.state && !['Active', 'Monitoring', 'Shadow', 'Disabled'].includes(props.state.status));
const statusLabel = computed(() => ({
  Active: 'Aktiv', Starting: 'Startet', Transition: 'Wechsel', Monitoring: 'Beobachtung', Shadow: 'Vorschau', Blocked: 'Wartet',
  Restoring: 'Rücknahme läuft', Stale: 'Vorschau veraltet', StaleExecution: 'Rückmeldung veraltet', RecoveryPending: 'Rücknahme ausstehend'
} as Record<string, string>)[props.state?.status ?? ''] ?? props.state?.status);

function startManualCharge(): void {
  const target = Number(manualTargetKwh.value);
  const maximum = manualMaximumKw.value === '' || manualMaximumKw.value === null ? null : Number(manualMaximumKw.value);
  emit('startManualCharge', target, maximum);
}

function confirmRecovery(): void {
  // Releasing the lock without device confirmation is a safety decision; require an explicit acknowledgement.
  if (window.confirm('Beide Speicher vor Ort geprüft? Die Einzelakku-Steuerung wird ohne Gerätebestätigung wieder freigegeben.')) {
    emit('confirmRecovery');
  }
}

function canConfirmRecovery(): boolean {
  return props.canControl && !!props.state?.recoveryPending && props.state.status !== 'Active';
}

function powerLabel(watts: number): string {
  return watts > 0 ? `Laden ${formatPower(watts)}` : watts < 0 ? `Entladen ${formatPower(-watts)}` : 'Halten';
}
</script>

<template>
  <v-card v-if="state && state.status !== 'Disabled'" class="joint-overview mb-4" variant="outlined">
    <div class="joint-heading">
      <div><strong>Gemeinsame Akkusteuerung</strong><v-chip size="x-small" :color="needsAttention ? 'warning' : state.isShadow ? 'info' : 'success'">{{ statusLabel }}</v-chip></div>
      <span class="joint-stamp">Stand {{ formatDateTime(state.updatedAtUtc) }}</span>
    </div>
    <div class="joint-readings" aria-label="Beide Akkus im Überblick">
      <div v-for="battery in state.batteries" :key="battery.id">
        <span>{{ battery.id === 'e3dc' ? 'E3/DC' : 'Victron' }}</span>
        <strong>{{ formatPercent(battery.socPercent) }}</strong>
        <span>{{ powerLabel(!state.isShadow ? battery.id === 'e3dc' ? state.execution.e3dcMeasuredWatts : state.execution.victronMeasuredWatts : battery.measuredPowerWatts) }} · gemessen</span>
      </div>
      <p v-if="!state.batteries.length">{{ state.localTelemetry ? `E3/DC ${formatPercent(state.localTelemetry.socPercent)}` : 'Noch keine aktuellen Akkuwerte' }}</p>
    </div>
    <p v-if="needsAttention" class="joint-status-message" role="status">{{ state.message }}</p>
      <v-alert v-if="canConfirmRecovery()" type="warning" variant="tonal" density="compact" class="mt-2">
        Die Rücknahme der gemeinsamen Steuerung ist noch nicht bestätigt; die Einzelakku-Steuerung bleibt gesperrt.
        Ist ein Gerät dauerhaft nicht erreichbar: gemeinsame Steuerung deaktivieren, beide Speicher vor Ort prüfen und dann bestätigen.
        <div class="mt-2">
          <v-btn size="small" color="warning" variant="flat" :loading="recoveryBusy" @click="confirmRecovery">
            Rücknahme manuell bestätigen
          </v-btn>
        </div>
      </v-alert>
      <section v-if="state.manualCharge.isActive" class="joint-manual-active" aria-label="Aktive manuelle Ladung">
        <strong>Manuelle Ladung aktiv</strong>
        <template v-if="state.manualCharge.isActive">
          <v-progress-linear class="mt-2" color="success" height="8" rounded
            :model-value="state.manualCharge.targetKwh > 0 ? Math.min(100, state.manualCharge.chargedKwh / state.manualCharge.targetKwh * 100) : 0" />
          <p class="mt-1">
            {{ formatNumber(state.manualCharge.chargedKwh, 2) }} von {{ formatNumber(state.manualCharge.targetKwh, 2) }} kWh geladen
            · {{ state.manualCharge.maximumPowerWatts > 0 ? `höchstens ${formatPower(state.manualCharge.maximumPowerWatts)}` : 'maximale zulässige Leistung' }}
            <template v-if="state.manualCharge.expiresAtUtc"> · endet spätestens {{ formatDateTime(state.manualCharge.expiresAtUtc) }}</template>
          </p>
          <v-btn v-if="canControl" size="small" variant="tonal" :loading="manualChargeBusy" @click="emit('stopManualCharge')">Manuelle Ladung beenden</v-btn>
        </template>
      </section>

    <details v-if="canControl && !state.isShadow && !state.manualCharge.isActive" class="joint-disclosure">
      <summary>Manuell laden</summary>
      <div class="joint-detail-body">
        <p>Beide Akkus laden, unabhängig vom Preis.</p>
        <p v-if="state.manualCharge.lastResult" class="text-caption">{{ state.manualCharge.lastResult }}</p>
        <div class="d-flex flex-wrap ga-2 align-center mt-2">
          <v-text-field v-model="manualTargetKwh" label="Lademenge" suffix="kWh" type="number" min="0.1" step="0.5"
            density="compact" hide-details style="max-width: 9rem" />
          <v-text-field v-model="manualMaximumKw" label="Höchstleistung (optional)" suffix="kW" type="number" min="0.1" step="0.5"
            density="compact" hide-details style="max-width: 13rem" />
          <v-btn size="small" color="primary" variant="flat" :loading="manualChargeBusy"
            :disabled="!(Number(manualTargetKwh) > 0)" @click="startManualCharge">Laden starten</v-btn>
        </div>
      </div>
    </details>
    <details class="joint-disclosure">
      <summary>Status & Planungsdetails <span v-if="state.warnings.length" class="joint-stamp">· {{ state.warnings.length }} Hinweise</span></summary>
      <div class="joint-detail-body">
        <p>{{ state.message }}</p>
      <p v-if="!state.isShadow" class="mt-2">
        Letzte Sollwerte: Victron {{ powerLabel(state.execution.victronTargetWatts) }} · E3/DC {{ powerLabel(state.execution.e3dcTargetWatts) }}<br>
        Gemessen: Victron {{ powerLabel(state.execution.victronMeasuredWatts) }} · E3/DC {{ powerLabel(state.execution.e3dcMeasuredWatts) }}<br>
        Ausführungsstand: {{ formatDateTime(state.execution.updatedAtUtc) }}
      </p>
      <p v-if="state.committedSlot" class="mt-1">
        <strong>Festgelegt bis {{ formatDateTime(state.committedSlot.endsAtUtc) }}:</strong>
        <template v-for="(target, index) in state.committedSlot.targets" :key="target.id">
          {{ index > 0 ? ' · ' : ' ' }}{{ target.id === 'e3dc' ? 'E3/DC' : 'Victron' }} {{ powerLabel(target.signedPowerWatts) }}
        </template>
        ({{ formatPrice(state.committedSlot.pricePerKwh, state.committedSlot.currency) }})
      </p>
      <p v-if="state.calculatedAtUtc" class="text-caption">Berechnet: {{ formatDateTime(state.calculatedAtUtc) }} · Netzbudget: {{ state.maximumGridImportWatts ? formatPower(state.maximumGridImportWatts) : 'keine zusätzliche Gesamtgrenze' }}</p>
      <v-row v-if="state.batteries.length" class="mt-2">
        <v-col v-for="battery in state.batteries" :key="battery.id" cols="12" sm="6">
          <strong>{{ battery.id === 'e3dc' ? 'E3/DC' : 'Victron' }}</strong>
          <p>{{ formatPercent(battery.socPercent) }} · {{ formatNumber(battery.capacityKwh, 2) }} kWh · Reserve {{ formatNumber(battery.reserveKwh, 2) }} kWh</p>
          <p>Gemessen: {{ powerLabel(battery.measuredPowerWatts) }}</p>
          <p v-if="battery.id === 'e3dc' && state.localTelemetry" class="text-caption">
            Vom Gerät gemeldete Grenze: Laden {{ formatPower(state.localTelemetry.effectiveChargeLimitWatts) }} ·
            Entladen {{ formatPower(state.localTelemetry.effectiveDischargeLimitWatts) }}
          </p>
          <p v-if="state.slots[0]">Vorschlag: {{ powerLabel(state.slots[0].targets.find(t => t.id === battery.id)?.signedPowerWatts ?? 0) }}</p>
        </v-col>
      </v-row>
      <p v-else-if="state.localTelemetry" class="mt-2">
        E3/DC: {{ formatPercent(state.localTelemetry.socPercent) }} ·
        {{ formatNumber(state.localTelemetry.usableCapacityKwh, 2) }} kWh ·
        Reserve {{ formatNumber(state.localTelemetry.reserveKwh, 2) }} kWh ·
        Messzeit {{ formatDateTime(state.localTelemetry.measuredAtUtc) }}
      </p>
      <ul v-if="state.warnings.length" class="pl-4 mt-2">
        <li v-for="warning in state.warnings" :key="warning" class="text-caption">{{ warning }}</li>
      </ul>
      <details v-if="state.slots.length" class="mt-3">
        <summary>Getrennte Ladeziele und Ladestandsverläufe anzeigen</summary>
        <div class="joint-plan-table">
          <table>
            <thead><tr><th>Zeit</th><th>Preis</th><th>Victron</th><th>E3/DC</th><th>Begründung</th></tr></thead>
            <tbody>
              <tr v-for="slot in state.slots" :key="slot.startsAtUtc">
                <td>{{ formatDateTime(slot.startsAtUtc) }}</td>
                <td>{{ formatPrice(slot.pricePerKwh, slot.currency) }}{{ slot.isPriceEstimated ? ' (geschätzt)' : '' }}</td>
                <td v-for="target in slot.targets" :key="target.id">
                  {{ powerLabel(target.signedPowerWatts) }}<br>{{ formatPercent(target.socAfterPercent) }}
                </td>
                <td>{{ slot.reason }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </details>
      </div>
    </details>
  </v-card>
</template>

<style scoped>
.joint-overview { max-width: 1180px; margin-inline: auto; padding: 14px 18px; color: var(--efp-text); background: var(--efp-surface, var(--efp-card)); }
.joint-heading, .joint-heading > div { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
.joint-heading { justify-content: space-between; gap: 6px 16px; font-size: .88rem; }
.joint-stamp { font-size: .72rem; color: var(--efp-muted); font-weight: 400; }
.joint-readings { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px 24px; margin-block: 12px; }
.joint-readings > div { display: flex; align-items: baseline; flex-wrap: wrap; gap: 4px 10px; font-size: .8rem; }
.joint-readings strong { font-size: 1.25rem; }
.joint-readings span:last-child { color: var(--efp-muted); }
.joint-disclosure { border-top: 1px solid var(--efp-border); padding-block: 9px 2px; margin-top: 7px; }
.joint-disclosure > summary { cursor: pointer; font-size: .8rem; font-weight: 600; }
.joint-disclosure > summary:focus-visible { outline: 2px solid var(--efp-primary); outline-offset: 3px; }
.joint-detail-body { margin-top: 12px; font-size: .85rem; }
.joint-manual-active, .joint-status-message { margin-block: 12px; padding: 12px; border-left: 3px solid var(--efp-primary); background: var(--efp-card-accent); font-size: .85rem; }
@media (max-width: 600px) { .joint-readings { grid-template-columns: minmax(0, 1fr); } .joint-overview { padding: 12px; } }
.joint-plan-table { overflow-x: auto; max-height: 28rem; }
table { border-collapse: collapse; width: 100%; font-size: .8rem; }
th, td { text-align: left; padding: .6rem; border-bottom: 1px solid rgba(128, 128, 128, .25); vertical-align: top; }
th { white-space: nowrap; }
</style>
