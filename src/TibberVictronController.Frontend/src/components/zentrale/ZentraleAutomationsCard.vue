<script setup lang="ts">
import { computed } from 'vue';
import { expertMode } from '../../uiMode';
import { automations } from './zentraleModel';
import { useZentraleData } from './useZentraleData';
import type { ZentraleTarget } from './zentraleTypes';

/** Active automations with their current action; a click explains the rule, its trigger and what comes next. */
withDefaults(defineProps<{ detailed?: boolean }>(), { detailed: false });
const emit = defineEmits<{ open: [target: ZentraleTarget] }>();
const data = useZentraleData();
const items = computed(() => automations(data));
const activeCount = computed(() => items.value.filter(i => i.enabled).length);
</script>

<template>
  <section class="z-card" aria-labelledby="z-auto-title">
    <header class="z-card__head">
      <h2 id="z-auto-title"><v-icon icon="mdi-robot-outline" size="22" class="z-auto__head-icon" />Automationen</h2>
      <span class="z-pill">Aktiv ({{ activeCount }})</span>
    </header>
    <ul class="z-auto">
      <li v-for="item in items" :key="item.id">
        <button type="button" class="z-auto__row" :aria-label="`${item.title}: ${item.stateLabel}. Details öffnen`" @click="emit('open', item.id)">
          <v-icon :icon="item.enabled ? 'mdi-check-circle' : item.enabled === false ? 'mdi-pause-circle-outline' : 'mdi-help-circle-outline'"
            :class="item.enabled ? 'z-auto__on' : 'z-auto__off'" size="20" />
          <span class="z-auto__text">
            <strong>{{ item.title }}</strong>
            <small>{{ item.enabled ? item.action : item.stateLabel }}</small>
            <small v-if="detailed" class="z-auto__state">{{ item.stateLabel }} · {{ item.devices }}</small>
            <small v-if="detailed && item.trigger" class="z-auto__why">Warum: {{ item.trigger }}</small>
            <small v-if="detailed && item.until">{{ item.until }}</small>
            <small v-if="detailed && expertMode && item.ruleId"><code>{{ item.ruleId }}</code></small>
            <small v-if="item.problem" class="z-auto__problem">{{ item.problem }}</small>
          </span>
          <v-icon icon="mdi-information-outline" size="18" class="z-auto__info" />
        </button>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.z-auto__head-icon { color: var(--efp-primary); }
.z-auto { list-style: none; margin: 0; padding: 0; }
.z-auto li + li { border-top: 1px solid var(--efp-border-soft); }
.z-auto__row { display: grid; grid-template-columns: 24px 1fr 20px; gap: 10px; align-items: start; width: 100%; padding: 9px 4px; text-align: left; color: var(--efp-text); background: none; border: 0; border-radius: 8px; cursor: pointer; }
.z-auto__row:hover { background: var(--efp-surface-muted); }
.z-auto__text { display: grid; gap: 1px; min-width: 0; }
.z-auto__text strong { font-size: .88rem; font-weight: 600; }
.z-auto__text small { font-size: .78rem; color: var(--efp-muted); line-height: 1.4; }
.z-auto__why { color: var(--efp-secondary) !important; }
.z-auto__problem { color: var(--efp-warning-text) !important; }
.z-auto__on { color: var(--efp-success); }
.z-auto__off { color: var(--efp-muted); }
.z-auto__info { color: var(--efp-muted); }
</style>
