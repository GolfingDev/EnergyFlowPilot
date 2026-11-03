<script setup lang="ts">
import { computed } from 'vue';
import { plannedActions } from './zentraleModel';
import { clock, when } from './zentraleFormat';
import { useZentraleData } from './useZentraleData';
import type { ZentraleTarget } from './zentraleTypes';

/** Next planned steps of all automations, from real plans only. */
const props = withDefaults(defineProps<{ limit?: number }>(), { limit: 6 });
const emit = defineEmits<{ open: [target: ZentraleTarget] }>();
const data = useZentraleData();
const actions = computed(() => plannedActions(data, props.limit));
</script>

<template>
  <section class="z-card" aria-labelledby="z-plan-title">
    <header class="z-card__head">
      <h2 id="z-plan-title"><v-icon icon="mdi-format-list-checks" size="22" class="z-plan__head-icon" />Geplante Aktionen</h2>
    </header>
    <p v-if="!actions.length" class="z-note">Im verfügbaren Plan ist kein weiterer Wechsel vorgesehen.</p>
    <ol v-else class="z-plan">
      <li v-for="action in actions" :key="action.key">
        <button type="button" class="z-plan__row" @click="emit('open', action.target)">
          <span class="z-plan__time"><v-icon icon="mdi-clock-outline" size="16" />{{ when(action.startsAtUtc, data.now.value) }}<template v-if="action.endsAtUtc">–{{ clock(action.endsAtUtc) }}</template></span>
          <span class="z-plan__text">
            <strong><v-icon :icon="action.icon" size="16" :style="{ color: action.color }" /> {{ action.title }}</strong>
            <small>{{ action.detail }}{{ action.estimated ? ' · Preis geschätzt' : '' }}</small>
          </span>
        </button>
      </li>
    </ol>
  </section>
</template>

<style scoped>
.z-plan__head-icon { color: var(--efp-primary); }
.z-plan { list-style: none; margin: 0; padding: 0; }
.z-plan li + li { border-top: 1px solid var(--efp-border-soft); }
.z-plan__row { display: grid; grid-template-columns: minmax(96px, auto) 1fr; gap: 12px; width: 100%; padding: 9px 4px; text-align: left; color: var(--efp-text); background: none; border: 0; border-radius: 8px; cursor: pointer; }
.z-plan__row:hover { background: var(--efp-surface-muted); }
.z-plan__time { display: flex; align-items: center; gap: 6px; font-size: .84rem; font-variant-numeric: tabular-nums; color: var(--efp-secondary); white-space: nowrap; }
.z-plan__text { display: grid; min-width: 0; }
.z-plan__text strong { font-size: .86rem; font-weight: 600; }
.z-plan__text small { font-size: .76rem; color: var(--efp-muted); line-height: 1.4; }
@media (max-width: 480px) { .z-plan__row { grid-template-columns: 1fr; gap: 2px; } }
</style>
