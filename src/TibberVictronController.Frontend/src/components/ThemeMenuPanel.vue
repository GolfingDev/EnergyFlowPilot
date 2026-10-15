<script setup lang="ts">
import { computed } from 'vue';
import { useTheme } from 'vuetify';
import { energyFlowThemes, type EnergyFlowThemeDefinition, type EnergyFlowThemeName } from '../themeRegistry';

const props = defineProps<{
  activeName: EnergyFlowThemeName;
}>();

const emit = defineEmits<{
  (event: 'select', name: EnergyFlowThemeName): void;
}>();

const theme = useTheme();

const groups = computed(() => [
  { title: 'Hell', themes: energyFlowThemes.filter((option) => !option.dark) },
  { title: 'Dunkel', themes: energyFlowThemes.filter((option) => option.dark) }
]);

/** Preview colours come from the registered Vuetify theme so the tile always matches the real design. */
function previewStyle(option: EnergyFlowThemeDefinition): Record<string, string> {
  const colors = theme.themes.value[option.name]?.colors ?? {};

  return {
    '--preview-background': String(colors.background ?? '#ffffff'),
    '--preview-surface': String(colors.surface ?? '#ffffff'),
    '--preview-primary': String(colors.primary ?? '#1f6fb2'),
    '--preview-success': String(colors.success ?? '#2e8540'),
    '--preview-warning': String(colors.warning ?? '#b26a00')
  };
}
</script>

<template>
  <div class="theme-panel" role="dialog" aria-label="Design auswählen">
    <div class="theme-panel__header">
      <strong>Design auswählen</strong>
      <span>Layout, Farben und Kontrast der Oberfläche.</span>
    </div>

    <section v-for="group in groups" :key="group.title" class="theme-panel__group">
      <h3>{{ group.title }}</h3>
      <div class="theme-panel__grid">
        <button
          v-for="option in group.themes"
          :key="option.name"
          type="button"
          class="theme-tile"
          :class="{ 'theme-tile--active': props.activeName === option.name }"
          :aria-pressed="props.activeName === option.name"
          @click="emit('select', option.name)"
        >
          <span class="theme-tile__preview" :style="previewStyle(option)" aria-hidden="true">
            <span class="theme-tile__bar" />
            <span class="theme-tile__cards">
              <span class="theme-tile__card"><span class="theme-tile__line theme-tile__line--primary" /></span>
              <span class="theme-tile__card"><span class="theme-tile__line theme-tile__line--success" /><span class="theme-tile__line theme-tile__line--warning" /></span>
            </span>
          </span>
          <span class="theme-tile__text">
            <strong>
              <v-icon :icon="option.icon" size="16" />
              {{ option.label }}
              <v-icon v-if="props.activeName === option.name" class="theme-tile__check" icon="mdi-check-circle" size="16" />
            </strong>
            <small>{{ option.description }}</small>
          </span>
        </button>
      </div>
    </section>
  </div>
</template>

<style scoped>
.theme-panel {
  width: min(640px, calc(100vw - 32px));
  max-height: min(80vh, 720px);
  overflow-y: auto;
  padding: 14px;
  border: 1px solid var(--efp-border);
  border-radius: 14px;
  background: var(--efp-surface);
  color: var(--efp-text);
  box-shadow: var(--efp-strong-shadow);
  display: grid;
  gap: 14px;
}

.theme-panel__header {
  display: grid;
  gap: 2px;
  padding-bottom: 10px;
  border-bottom: 1px solid var(--efp-border-soft);
}

.theme-panel__header strong { font-size: 1rem; }
.theme-panel__header span { color: var(--efp-muted); font-size: 0.8rem; }

.theme-panel__group { display: grid; gap: 8px; }

.theme-panel__group h3 {
  margin: 0;
  font-size: 0.72rem;
  font-weight: 700;
  letter-spacing: 0.1em;
  text-transform: uppercase;
  color: var(--efp-muted);
}

.theme-panel__grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
  gap: 10px;
}

.theme-tile {
  display: grid;
  gap: 8px;
  padding: 8px;
  border: 2px solid var(--efp-border-soft);
  border-radius: 12px;
  background: var(--efp-surface);
  color: var(--efp-text);
  text-align: left;
  cursor: pointer;
}

.theme-tile:hover { border-color: var(--efp-border); }
.theme-tile:focus-visible { outline: 2px solid var(--efp-primary); outline-offset: 2px; }
.theme-tile--active { border-color: var(--efp-primary); }

.theme-tile__preview {
  display: grid;
  grid-template-rows: 10px 1fr;
  gap: 6px;
  height: 72px;
  padding: 6px;
  border-radius: 8px;
  background: var(--preview-background);
  border: 1px solid var(--efp-border-soft);
}

.theme-tile__bar { border-radius: 3px; background: var(--preview-surface); box-shadow: inset 0 -2px 0 var(--preview-primary); }
.theme-tile__cards { display: grid; grid-template-columns: 1.4fr 1fr; gap: 6px; }
.theme-tile__card { display: grid; align-content: end; gap: 4px; padding: 6px; border-radius: 4px; background: var(--preview-surface); }
.theme-tile__line { display: block; height: 4px; border-radius: 2px; }
.theme-tile__line--primary { background: var(--preview-primary); width: 80%; }
.theme-tile__line--success { background: var(--preview-success); width: 70%; }
.theme-tile__line--warning { background: var(--preview-warning); width: 45%; }

.theme-tile__text { display: grid; gap: 2px; min-width: 0; }
.theme-tile__text strong { display: flex; align-items: center; gap: 6px; font-size: 0.86rem; }
.theme-tile__check { margin-left: auto; color: var(--efp-primary); }
.theme-tile__text small { color: var(--efp-muted); font-size: 0.74rem; line-height: 1.35; }
</style>
