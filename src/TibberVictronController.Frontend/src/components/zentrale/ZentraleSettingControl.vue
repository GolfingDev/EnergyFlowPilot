<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { isAdmin } from '../../auth';
import { errorText } from './zentraleApi';
import { clock } from './zentraleFormat';
import { useZentraleData } from './useZentraleData';

/*
 * Edits one persisted controller setting in place. The feedback separates the four states the user cares about:
 * changed here (draft) → sent → stored and read back from the backend → used by a new calculation of the controller.
 */
const props = withDefaults(defineProps<{
  settingKey: string;
  label: string;
  kind: 'slider' | 'switch';
  min?: number;
  max?: number;
  step?: number;
  /** Display factor: stored watts shown as kW use 0.001. */
  scale?: number;
  unit?: string;
  hint?: string;
  /** Asked before a switch changes; for settings that stop or start real device control. */
  confirmText?: string;
  inverted?: boolean;
  /** Extra validation against other settings, returns a German message or null. */
  validate?: (storedValue: number) => string | null;
}>(), { min: 0, max: 100, step: 1, scale: 1, unit: '', inverted: false });

const data = useZentraleData();
const stored = computed(() => data.setting(props.settingKey));
const storedNumber = computed(() => stored.value == null || stored.value === '' ? null : Number(stored.value));
const storedSwitch = computed(() => (stored.value === 'true') !== props.inverted);
const draft = ref<number | null>(null);
const phase = ref<'idle' | 'sending' | 'confirmed' | 'error'>('idle');
const confirmedAt = ref<number | null>(null);
const error = ref<string | null>(null);

watch(storedNumber, (value) => { if (phase.value !== 'sending') draft.value = value == null ? null : value * props.scale; }, { immediate: true });

const sliderMax = computed(() => Math.max(props.max, draft.value ?? props.min));
const dirty = computed(() => props.kind === 'slider' && draft.value != null && storedNumber.value != null
  && Math.abs(draft.value - storedNumber.value * props.scale) > props.step / 1000);
const validationError = computed(() => dirty.value && draft.value != null && props.validate ? props.validate(toStored(draft.value)) : null);
/** A newer decision or joint plan than the confirmation means the controller has calculated with the new value. */
const applied = computed(() => {
  if (phase.value !== 'confirmed' || confirmedAt.value == null) return false;
  const stamps = [data.state.summary?.current.decidedAtUtc, data.state.shadow?.calculatedAtUtc].filter((s): s is string => !!s).map(Date.parse);
  return stamps.some(stamp => stamp > confirmedAt.value!);
});
const display = (value: number | null) => value == null ? '–'
  : `${value.toLocaleString('de-DE', { maximumFractionDigits: props.step < 1 ? 1 : 0 })}${props.unit ? ` ${props.unit}` : ''}`;

function toStored(displayValue: number): number {
  const value = displayValue / props.scale;
  return props.step >= 1 && props.scale === 1 ? Math.round(value) : Math.round(value * 1000) / 1000;
}

async function send(value: string): Promise<void> {
  phase.value = 'sending';
  error.value = null;
  try {
    await data.saveSetting(props.settingKey, value);
    if (!sameValue(data.setting(props.settingKey), value)) throw new Error('Der Wert wurde nicht übernommen; das Backend meldet weiterhin den alten Wert.');
    confirmedAt.value = Date.now();
    phase.value = 'confirmed';
    // The controller recalculates on its own cycle; fetch the next decision a little earlier to show it.
    window.setTimeout(() => void data.refreshFast(), 5_000);
  } catch (reason) {
    phase.value = 'error';
    error.value = errorText(reason, 'Einstellung wurde nicht gespeichert.');
    draft.value = storedNumber.value == null ? null : storedNumber.value * props.scale;
  }
}

// The backend may normalise numbers ("5000" vs "5000.0"); compare numerically where possible.
function sameValue(actual: string | null, expected: string): boolean {
  if (actual === expected) return true;
  const a = Number(actual);
  const b = Number(expected);
  return actual != null && actual !== '' && Number.isFinite(a) && Number.isFinite(b) && Math.abs(a - b) < 1e-9;
}

function apply(): void {
  if (draft.value == null || validationError.value) return;
  void send(String(toStored(draft.value)));
}

function reset(): void {
  draft.value = storedNumber.value == null ? null : storedNumber.value * props.scale;
}

function toggle(next: boolean | null): void {
  if (props.confirmText && !window.confirm(props.confirmText)) return;
  void send(String(Boolean(next) !== props.inverted));
}
</script>

<template>
  <div class="z-setting" :class="{ 'z-setting--dirty': dirty }">
    <div class="z-setting__head">
      <label :for="`z-${settingKey}`">{{ label }}</label>
      <strong v-if="kind === 'slider'">{{ display(dirty ? draft : storedNumber == null ? null : storedNumber * scale) }}</strong>
    </div>

    <template v-if="isAdmin">
      <v-slider v-if="kind === 'slider'" :id="`z-${settingKey}`" :model-value="draft ?? min" :min="min" :max="sliderMax" :step="step" :disabled="phase === 'sending' || storedNumber == null"
        color="primary" density="compact" hide-details thumb-label :aria-label="label" @update:model-value="draft = Number($event)" />
      <v-switch v-else :id="`z-${settingKey}`" :model-value="storedSwitch" :loading="phase === 'sending'" :disabled="phase === 'sending' || stored == null" color="primary"
        density="compact" hide-details inset :label="storedSwitch ? 'Ein' : 'Aus'" @update:model-value="toggle" />
    </template>
    <p v-else class="z-setting__readonly">{{ kind === 'switch' ? (storedSwitch ? 'Ein' : 'Aus') : '' }} Ändern nur mit der Rolle Admin.</p>

    <p v-if="hint" class="z-setting__hint">{{ hint }}</p>
    <p v-if="validationError" class="z-setting__error" role="alert">{{ validationError }}</p>

    <div v-if="dirty" class="z-setting__actions">
      <span>Geändert, noch nicht gesendet</span>
      <v-btn size="small" variant="text" @click="reset">Verwerfen</v-btn>
      <v-btn size="small" color="primary" variant="flat" :disabled="!!validationError" @click="apply">Übernehmen</v-btn>
    </div>
    <ol v-else-if="phase !== 'idle'" class="z-setting__steps" aria-live="polite">
      <li :class="{ done: phase !== 'sending' && phase !== 'error', busy: phase === 'sending', failed: phase === 'error' }">Gesendet</li>
      <li :class="{ done: phase === 'confirmed' }">Gespeichert</li>
      <li :class="{ done: applied }">{{ applied ? 'Vom Regler neu berechnet' : 'Wartet auf nächste Berechnung' }}</li>
    </ol>
    <p v-if="error" class="z-setting__error" role="alert">{{ error }}</p>
    <p v-else-if="phase === 'confirmed' && confirmedAt" class="z-setting__hint">Gespeichert um {{ clock(new Date(confirmedAt).toISOString()) }}.</p>
  </div>
</template>

<style scoped>
.z-setting { padding: 10px 0; border-bottom: 1px solid var(--efp-border-soft); }
.z-setting:last-child { border-bottom: 0; }
.z-setting__head { display: flex; justify-content: space-between; gap: 12px; align-items: baseline; }
.z-setting__head label { font-size: .86rem; color: var(--efp-text); }
.z-setting__head strong { font-variant-numeric: tabular-nums; font-size: .95rem; }
.z-setting--dirty .z-setting__head strong { color: var(--efp-warning-text); }
.z-setting__hint, .z-setting__readonly { margin: 2px 0 0; font-size: .76rem; color: var(--efp-muted); line-height: 1.4; }
.z-setting__error { margin: 4px 0 0; font-size: .78rem; color: var(--efp-error); }
.z-setting__actions { display: flex; align-items: center; gap: 4px; justify-content: flex-end; font-size: .76rem; color: var(--efp-warning-text); }
.z-setting__actions span { margin-right: auto; }
.z-setting__steps { display: flex; flex-wrap: wrap; gap: 4px 14px; margin: 6px 0 0; padding: 0; list-style: none; font-size: .74rem; color: var(--efp-muted); }
.z-setting__steps li::before { content: '○ '; }
.z-setting__steps li.done { color: var(--efp-success); }
.z-setting__steps li.done::before { content: '✓ '; }
.z-setting__steps li.busy::before { content: '… '; }
.z-setting__steps li.failed { color: var(--efp-error); }
.z-setting__steps li.failed::before { content: '✕ '; }
</style>
