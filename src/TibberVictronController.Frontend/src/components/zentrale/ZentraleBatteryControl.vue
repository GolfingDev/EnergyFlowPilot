<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { isAdmin } from '../../auth';
import { expertMode } from '../../uiMode';
import ZentraleSettingControl from './ZentraleSettingControl.vue';
import { errorText } from './zentraleApi';
import { age, batteryActivity, kw, kwh, percent, when } from './zentraleFormat';
import { jointStatusLabels } from './zentraleModel';
import { useZentraleData } from './useZentraleData';

/*
 * Everything that steers one storage, reachable with one click from the energy flow: operating mode,
 * charge limit and power limits. Only controls the backend really offers are shown.
 */
const props = defineProps<{ batteryId: 'victron' | 'e3dc' }>();
const data = useZentraleData();
const { state } = data;

const battery = computed(() => data.batteries.value.find(b => b.id === props.batteryId)!);
const activity = computed(() => batteryActivity(battery.value.watts));
const joint = computed(() => data.jointControlActive.value);
const dryRun = computed(() => data.setting('victron.dryRun') === 'true');
/** Without joint control the E3/DC runs its own regulation; the controller cannot send it a command. */
const steerable = computed(() => joint.value || props.batteryId === 'victron');
const manualActive = computed(() => joint.value ? !!state.shadow?.manualCharge.isActive : !!state.legacyManualCharge?.isActive);
const selectedMode = ref<'auto' | 'manual'>('auto');
watch(manualActive, (active) => { selectedMode.value = active ? 'manual' : 'auto'; }, { immediate: true });

const maximumChargeKw = computed(() => Number(data.setting('battery.maximumChargePowerWatts') ?? 0) / 1000 || 5);
const manualTargetKwh = ref(2);
const manualMaximumKw = ref(0);
const legacyMinutes = ref(30);
const legacyPowerKw = ref(2.5);
const command = ref<'idle' | 'sending' | 'accepted' | 'error'>('idle');
const commandError = ref<string | null>(null);
const sentAt = ref<number | null>(null);

const manualProgress = computed(() => {
  const manual = state.shadow?.manualCharge;
  return joint.value && manual?.isActive && manual.targetKwh > 0 ? Math.min(100, manual.chargedKwh / manual.targetKwh * 100) : null;
});
const manualEndsAt = computed(() => joint.value ? state.shadow?.manualCharge.expiresAtUtc ?? null : state.legacyManualCharge?.expiresAtUtc ?? null);
const deviceConfirmsCharging = computed(() => manualActive.value && (battery.value.watts ?? 0) > 50);

const hardMinimum = computed(() => Number(data.setting('battery.minimumStateOfChargePercent') ?? 0));
const planningMinimum = computed(() => Number(data.setting('battery.planningMinimumStateOfChargePercent') ?? 0));
const planningMaximum = computed(() => Number(data.setting('battery.planningMaximumStateOfChargePercent') ?? 100));
const e3dcLimits = computed(() => state.shadow?.localTelemetry ?? null);

async function run(action: () => Promise<void>): Promise<void> {
  command.value = 'sending';
  commandError.value = null;
  sentAt.value = Date.now();
  try {
    await action();
    command.value = 'accepted';
  } catch (reason) {
    command.value = 'error';
    commandError.value = errorText(reason, 'Befehl wurde nicht ausgeführt.');
  }
}

function startManual(): void {
  void run(() => joint.value
    ? data.startJointManualCharge(manualTargetKwh.value, manualMaximumKw.value > 0 ? manualMaximumKw.value : null)
    : data.startLegacyManualCharge(legacyMinutes.value, legacyPowerKw.value));
}

function stopManual(): void {
  void run(() => joint.value ? data.stopJointManualCharge() : data.stopLegacyManualCharge());
}

function chooseMode(mode: 'auto' | 'manual' | null): void {
  if (!mode) return;
  // Switching back to "Automatik" while a manual charge runs is the stop command; otherwise it only changes the form.
  if (mode === 'auto' && manualActive.value) stopManual();
  selectedMode.value = mode;
}
</script>

<template>
  <div class="z-battery">
    <section class="z-battery__now" aria-label="Aktueller Zustand">
      <div class="z-battery__soc">
        <strong>{{ percent(battery.soc) }}</strong>
        <span :class="`z-battery__activity z-battery__activity--${activity.kind}`">{{ activity.label }}{{ activity.kind === 'charge' || activity.kind === 'discharge' ? ` · ${kw(battery.watts)}` : '' }}</span>
      </div>
      <div class="z-battery__bar" role="meter" :aria-valuenow="battery.soc ?? undefined" aria-valuemin="0" aria-valuemax="100" :aria-label="`Ladezustand ${battery.name}`">
        <span :style="{ width: `${Math.max(0, Math.min(100, battery.soc ?? 0))}%` }" />
      </div>
      <dl class="z-facts">
        <div v-if="battery.usableKwh != null"><dt>Nutzbar über Reserve</dt><dd>{{ kwh(battery.usableKwh) }}</dd></div>
        <div v-if="battery.targetWatts != null"><dt>Sollwert der Steuerung</dt><dd>{{ battery.targetWatts > 50 ? `Laden ${kw(battery.targetWatts)}` : battery.targetWatts < -50 ? `Entladen ${kw(battery.targetWatts)}` : 'Halten' }}</dd></div>
        <div><dt>Gemessen</dt><dd>{{ data.live.value ? age(data.live.value.measuredAtUtc, data.now.value) : 'keine Messung' }}</dd></div>
      </dl>
      <p v-if="data.isStale.value" class="z-note z-note--warning">Messwerte sind veraltet – der angezeigte Zustand ist nicht gesichert.</p>
    </section>

    <section class="z-block" aria-labelledby="z-mode-title">
      <h3 id="z-mode-title">Betriebsmodus</h3>
      <p v-if="dryRun" class="z-note z-note--warning">Simulationsmodus: Entscheidungen werden berechnet, aber nicht an die Hardware gesendet.</p>
      <p v-if="!steerable" class="z-note">Akku 2 (E3/DC) regelt sich selbst. Steuern lässt er sich nur über die gemeinsame Akkusteuerung (Einstellungen → Ladestrategie).</p>
      <template v-else>
        <v-btn-toggle :model-value="selectedMode" mandatory divided density="comfortable" color="primary" variant="outlined" class="z-mode" :disabled="!isAdmin || command === 'sending'"
          aria-label="Betriebsmodus wählen" @update:model-value="chooseMode">
          <v-btn value="auto" prepend-icon="mdi-robot-outline">Automatik</v-btn>
          <v-btn value="manual" prepend-icon="mdi-lightning-bolt">Sofort laden</v-btn>
        </v-btn-toggle>
        <p v-if="!isAdmin" class="z-note">Lesezugriff: Modus ändern nur mit der Rolle Admin.</p>

        <p v-if="selectedMode === 'auto' && !manualActive" class="z-note">
          Die Akku-Optimierung entscheidet nach Strompreis, PV-Prognose und Reserve.
          <template v-if="state.summary?.overview">Jetzt: {{ state.summary.overview.reason }}</template>
        </p>

        <div v-if="manualActive" class="z-manual-active" role="status">
          <strong>Manuelle Ladung aktiv</strong>
          <v-progress-linear v-if="manualProgress != null" :model-value="manualProgress" color="primary" height="8" rounded class="my-2" />
          <p v-if="joint && state.shadow">{{ kwh(state.shadow.manualCharge.chargedKwh, 2) }} von {{ kwh(state.shadow.manualCharge.targetKwh, 2) }} geladen · gilt für beide Akkus</p>
          <p v-else-if="state.legacyManualCharge">{{ kw(state.legacyManualCharge.powerWatts) }} · nur Akku 1</p>
          <p>Hat Vorrang vor der Automatik. Endet spätestens {{ when(manualEndsAt) }}; danach übernimmt wieder die Akku-Optimierung.</p>
          <v-btn v-if="isAdmin" size="small" variant="tonal" :loading="command === 'sending'" @click="stopManual">Beenden und zur Automatik</v-btn>
        </div>

        <div v-else-if="selectedMode === 'manual' && isAdmin" class="z-manual-form">
          <template v-if="joint">
            <p class="z-note">Lädt Akku 1 und Akku 2 gemeinsam, unabhängig vom Preis – bis die Menge erreicht ist oder beide ihr Planungsmaximum haben.</p>
            <div class="z-slider-row"><label for="z-manual-kwh">Lademenge</label><strong>{{ kwh(manualTargetKwh) }}</strong></div>
            <v-slider id="z-manual-kwh" v-model="manualTargetKwh" :min="0.5" :max="20" :step="0.5" color="primary" density="compact" hide-details />
            <div class="z-slider-row"><label for="z-manual-kw">Höchstleistung</label><strong>{{ manualMaximumKw > 0 ? `${manualMaximumKw.toLocaleString('de-DE')} kW` : 'maximal zulässig' }}</strong></div>
            <v-slider id="z-manual-kw" v-model="manualMaximumKw" :min="0" :max="10" :step="0.5" color="primary" density="compact" hide-details />
          </template>
          <template v-else>
            <p class="z-note">Lädt nur Akku 1 (Victron) mit fester Leistung für die gewählte Dauer.</p>
            <div class="z-slider-row"><label for="z-legacy-min">Dauer</label><strong>{{ legacyMinutes }} min</strong></div>
            <v-slider id="z-legacy-min" v-model="legacyMinutes" :min="15" :max="240" :step="15" color="primary" density="compact" hide-details />
            <div class="z-slider-row"><label for="z-legacy-kw">Ladeleistung</label><strong>{{ legacyPowerKw.toLocaleString('de-DE') }} kW</strong></div>
            <v-slider id="z-legacy-kw" v-model="legacyPowerKw" :min="0.5" :max="Math.max(0.5, maximumChargeKw)" :step="0.5" color="primary" density="compact" hide-details />
          </template>
          <v-btn color="primary" variant="flat" block prepend-icon="mdi-lightning-bolt" :loading="command === 'sending'" @click="startManual">Laden starten</v-btn>
        </div>

        <ol v-if="command !== 'idle'" class="z-steps" aria-live="polite">
          <li :class="{ done: command === 'accepted', busy: command === 'sending', failed: command === 'error' }">Befehl gesendet</li>
          <li :class="{ done: command === 'accepted' }">Vom Backend angenommen</li>
          <li v-if="selectedMode === 'manual' || manualActive" :class="{ done: manualActive }">Ladung aktiv gemeldet</li>
          <li v-if="selectedMode === 'manual' || manualActive" :class="{ done: deviceConfirmsCharging }">{{ deviceConfirmsCharging ? `Akku misst ${kw(battery.watts)} Laden` : 'Wartet auf Messwert vom Akku' }}</li>
        </ol>
        <p v-if="commandError" class="z-note z-note--error" role="alert">{{ commandError }}</p>
        <p v-if="state.shadow?.manualCharge.lastResult && !manualActive && joint" class="z-note">Letzte manuelle Ladung: {{ state.shadow.manualCharge.lastResult }}</p>
      </template>
    </section>

    <section class="z-block" aria-labelledby="z-limit-title">
      <h3 id="z-limit-title">Ladegrenze & Reserve</h3>
      <template v-if="batteryId === 'victron'">
        <ZentraleSettingControl setting-key="battery.planningMaximumStateOfChargePercent" label="Ladegrenze (Planungsmaximum)" kind="slider" :min="50" :max="100" unit="%"
          hint="Bis hierhin lädt die Automatik. Etwas Abstand zu 100 % lässt Platz für unerwarteten PV-Überschuss."
          :validate="v => v <= planningMinimum ? 'Die Ladegrenze muss über der Reserve liegen.' : null" />
        <ZentraleSettingControl setting-key="battery.planningMinimumStateOfChargePercent" label="Reserve (Planungsminimum)" kind="slider" :min="hardMinimum" :max="90" unit="%"
          hint="Unter diesen Ladestand entlädt die Automatik nicht."
          :validate="v => v < hardMinimum ? 'Die Reserve darf das harte Minimum nicht unterschreiten.' : v >= planningMaximum ? 'Die Reserve muss unter der Ladegrenze liegen.' : null" />
      </template>
      <template v-else>
        <ZentraleSettingControl setting-key="jointBattery.e3dc.maximumSocPercent" label="Ladegrenze in der gemeinsamen Planung" kind="slider" :min="50" :max="100" unit="%"
          hint="Gilt, wenn die gemeinsame Akkusteuerung plant. Die eigene Reserve meldet das E3/DC selbst." />
        <dl v-if="battery.reserveKwh != null" class="z-facts"><div><dt>Vom Gerät gemeldete Reserve</dt><dd>{{ kwh(battery.reserveKwh) }}</dd></div></dl>
      </template>
    </section>

    <section class="z-block" aria-labelledby="z-power-title">
      <h3 id="z-power-title">Leistungsbegrenzung</h3>
      <template v-if="batteryId === 'victron'">
        <ZentraleSettingControl setting-key="battery.maximumChargePowerWatts" label="Maximale Ladeleistung" kind="slider" :min="0.1" :max="10" :step="0.1" :scale="0.001" unit="kW" />
        <ZentraleSettingControl setting-key="battery.maximumDischargePowerWatts" label="Maximale Entladeleistung" kind="slider" :min="0.1" :max="10" :step="0.1" :scale="0.001" unit="kW" />
      </template>
      <template v-else>
        <dl class="z-facts">
          <div><dt>Ladegrenze laut Gerät</dt><dd>{{ kw(e3dcLimits?.effectiveChargeLimitWatts) }}</dd></div>
          <div><dt>Entladegrenze laut Gerät</dt><dd>{{ kw(e3dcLimits?.effectiveDischargeLimitWatts) }}</dd></div>
        </dl>
        <p class="z-note">Das E3/DC meldet seine Leistungsgrenzen selbst; der Controller setzt dafür keinen eigenen Wert.</p>
        <ZentraleSettingControl v-if="expertMode" setting-key="jointBattery.e3dc.respectReportedLimits" label="Gemeldete Grenzen in der Planung beachten" kind="switch" />
      </template>
    </section>

    <section v-if="expertMode" class="z-block z-block--expert" aria-labelledby="z-expert-title">
      <h3 id="z-expert-title">Expertendetails</h3>
      <dl class="z-facts">
        <div v-if="battery.capacityKwh != null"><dt>Kapazität (Planung)</dt><dd>{{ kwh(battery.capacityKwh) }}</dd></div>
        <div v-if="state.shadow"><dt>Gemeinsame Steuerung</dt><dd>{{ jointStatusLabels[state.shadow.status] ?? state.shadow.status }}</dd></div>
        <div v-if="state.shadow?.execution.validUntilUtc"><dt>Sollwert gültig bis</dt><dd>{{ when(state.shadow.execution.validUntilUtc) }}</dd></div>
        <div v-if="state.summary?.current.ruleId"><dt>Regel</dt><dd><code>{{ state.summary.current.ruleId }}</code></dd></div>
        <div v-if="batteryId === 'victron'"><dt>Hartes Minimum</dt><dd>{{ percent(hardMinimum) }}</dd></div>
      </dl>
      <p v-if="state.shadow?.message" class="z-note">{{ state.shadow.message }}</p>
      <ZentraleSettingControl v-if="batteryId === 'victron'" setting-key="battery.minimumStateOfChargePercent" label="Hartes Minimum (Akkuschutz)" kind="slider" :min="0" :max="50" unit="%"
        hint="Sicherheitsrelevant: harte Untergrenze zum Schutz des Akkus."
        :validate="v => v > planningMinimum ? 'Das harte Minimum darf nicht über der Reserve liegen.' : null" />
    </section>

    <router-link v-if="isAdmin" class="z-link" :to="{ name: 'settings', query: { section: 'battery' } }">Alle Akku-Einstellungen <v-icon icon="mdi-arrow-right" size="16" /></router-link>
  </div>
</template>

<style scoped>
.z-battery { display: grid; gap: 14px; }
.z-battery__soc { display: flex; align-items: baseline; justify-content: space-between; gap: 10px; }
.z-battery__soc strong { font-size: 2rem; font-weight: 650; font-variant-numeric: tabular-nums; }
.z-battery__activity { font-size: .9rem; color: var(--efp-muted); }
.z-battery__activity--charge, .z-battery__activity--discharge { color: var(--z-battery); }
.z-battery__bar { height: 8px; border-radius: 99px; background: var(--efp-border-soft); overflow: hidden; margin: 6px 0 10px; }
.z-battery__bar span { display: block; height: 100%; background: var(--z-battery); border-radius: inherit; transition: width .6s ease; }
.z-mode { width: 100%; margin-bottom: 8px; }
.z-mode :deep(.v-btn) { flex: 1 1 0; }
.z-manual-active { padding: 12px; border-radius: 10px; background: var(--efp-card-accent); border: 1px solid var(--efp-border); }
.z-manual-active p { margin: 2px 0 8px; font-size: .82rem; color: var(--efp-muted); }
.z-manual-form { display: grid; gap: 2px; }
.z-manual-form .v-btn { margin-top: 10px; }
.z-slider-row { display: flex; justify-content: space-between; font-size: .86rem; margin-top: 6px; }
.z-slider-row strong { font-variant-numeric: tabular-nums; }
</style>
