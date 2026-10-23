<script setup lang="ts">
import { formatDateTime, formatNumber, formatPercent, formatPower, formatPrice } from './dashboardFormatters';
import type { JointBatteryShadowDto } from './dashboardTypes';

const props = defineProps<{ state: JointBatteryShadowDto | null; recoveryBusy?: boolean }>();
const emit = defineEmits<{ confirmRecovery: [] }>();

function confirmRecovery(): void {
  // Releasing the lock without device confirmation is a safety decision; require an explicit acknowledgement.
  if (window.confirm('Beide Speicher vor Ort geprüft? Die Einzelakku-Steuerung wird ohne Gerätebestätigung wieder freigegeben.')) {
    emit('confirmRecovery');
  }
}

function canConfirmRecovery(): boolean {
  return !!props.state?.recoveryPending && props.state.status !== 'Active';
}

function powerLabel(watts: number): string {
  return watts > 0 ? `Laden ${formatPower(watts)}` : watts < 0 ? `Entladen ${formatPower(-watts)}` : 'Halten';
}
</script>

<template>
  <v-card v-if="state && state.status !== 'Disabled'" class="mb-4" variant="outlined">
    <v-card-title>Gemeinsame Akkusteuerung <v-chip size="small" :color="state.isShadow ? 'info' : state.status === 'Active' ? 'success' : 'warning'">{{ state.isShadow ? 'Simulation' : state.status }}</v-chip></v-card-title>
    <v-card-text>
      <p role="status">{{ state.message }}</p>
      <v-alert v-if="canConfirmRecovery()" type="warning" variant="tonal" density="compact" class="mt-2">
        Die Rücknahme der gemeinsamen Steuerung ist noch nicht bestätigt; die Einzelakku-Steuerung bleibt gesperrt.
        Ist ein Gerät dauerhaft nicht erreichbar: gemeinsame Steuerung deaktivieren, beide Speicher vor Ort prüfen und dann bestätigen.
        <div class="mt-2">
          <v-btn size="small" color="warning" variant="flat" :loading="recoveryBusy" @click="confirmRecovery">
            Rücknahme manuell bestätigen
          </v-btn>
        </div>
      </v-alert>
      <p v-if="!state.isShadow" class="mt-2">
        Letzte Sollwerte: Victron {{ powerLabel(state.execution.victronTargetWatts) }} · E3/DC {{ powerLabel(state.execution.e3dcTargetWatts) }}<br>
        Gemessen: Victron {{ powerLabel(state.execution.victronMeasuredWatts) }} · E3/DC {{ powerLabel(state.execution.e3dcMeasuredWatts) }}<br>
        Ausführungsstand: {{ formatDateTime(state.execution.updatedAtUtc) }}
      </p>
      <p v-if="state.calculatedAtUtc" class="text-caption">Berechnet: {{ formatDateTime(state.calculatedAtUtc) }} · Netzbudget: {{ state.maximumGridImportWatts ? formatPower(state.maximumGridImportWatts) : 'keine zusätzliche Gesamtgrenze' }}</p>
      <v-row v-if="state.batteries.length" class="mt-2">
        <v-col v-for="battery in state.batteries" :key="battery.id" cols="12" sm="6">
          <strong>{{ battery.id === 'e3dc' ? 'E3/DC' : 'Victron' }}</strong>
          <p>{{ formatPercent(battery.socPercent) }} · {{ formatNumber(battery.capacityKwh, 2) }} kWh · Reserve {{ formatNumber(battery.reserveKwh, 2) }} kWh</p>
          <p>Gemessen: {{ powerLabel(battery.measuredPowerWatts) }}</p>
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
    </v-card-text>
  </v-card>
</template>

<style scoped>
.joint-plan-table { overflow-x: auto; max-height: 28rem; }
table { border-collapse: collapse; width: 100%; font-size: .8rem; }
th, td { text-align: left; padding: .6rem; border-bottom: 1px solid rgba(128, 128, 128, .25); vertical-align: top; }
th { white-space: nowrap; }
</style>
