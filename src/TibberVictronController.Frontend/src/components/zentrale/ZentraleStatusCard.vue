<script setup lang="ts">
import { computed } from 'vue';
import { statusLines } from './zentraleModel';
import { useZentraleData } from './useZentraleData';
import type { ZentraleTarget } from './zentraleTypes';

/** "What is happening and why" in a few sentences built from measurements and backend reasons. */
const emit = defineEmits<{ open: [target: ZentraleTarget] }>();
const data = useZentraleData();
const lines = computed(() => statusLines(data));
const worst = computed(() => lines.value.some(l => l.severity === 'critical') ? 'critical' : lines.value.some(l => l.severity === 'warning') ? 'warning' : 'ok');
</script>

<template>
  <section class="z-card" aria-labelledby="z-status-title">
    <header class="z-card__head">
      <h2 id="z-status-title">
        <v-icon :icon="worst === 'ok' ? 'mdi-check-circle' : worst === 'warning' ? 'mdi-alert' : 'mdi-alert-octagon'" :class="`z-state z-state--${worst}`" size="22" />
        Aktueller Status
      </h2>
    </header>
    <ul class="z-status">
      <li v-for="(line, index) in lines" :key="index" :class="`z-status--${line.severity}`">
        <button v-if="line.target" type="button" class="z-status__row" @click="emit('open', line.target)">
          <v-icon :icon="line.icon" :style="{ color: line.color }" size="18" />
          <span>{{ line.text }}</span>
        </button>
        <div v-else class="z-status__row">
          <v-icon :icon="line.icon" :style="{ color: line.color }" size="18" />
          <span>{{ line.text }}</span>
        </div>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.z-status { list-style: none; margin: 0; padding: 0; display: grid; gap: 2px; }
.z-status__row { display: grid; grid-template-columns: 22px 1fr; gap: 10px; align-items: start; width: 100%; padding: 6px 4px; border-radius: 8px; text-align: left;
  color: var(--efp-text); font-size: .87rem; line-height: 1.45; background: none; border: 0; }
button.z-status__row { cursor: pointer; }
button.z-status__row:hover { background: var(--efp-surface-muted); }
.z-status--warning .z-status__row { color: var(--efp-warning-text); }
.z-status--critical .z-status__row { color: var(--efp-error); font-weight: 600; }
.z-state--ok { color: var(--efp-success); }
.z-state--warning { color: var(--efp-warning); }
.z-state--critical { color: var(--efp-error); }
</style>
