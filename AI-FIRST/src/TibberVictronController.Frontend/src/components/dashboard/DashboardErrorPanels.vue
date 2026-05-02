<script setup lang="ts">
import type { DashboardLoadError } from './dashboardTypes';

defineProps<{
  errors: DashboardLoadError[];
}>();
</script>

<template>
  <v-expansion-panels v-if="errors.length" class="error-panels" variant="accordion">
    <v-expansion-panel v-for="error in errors" :key="`${error.source}-${error.message}`" class="error-panel">
      <v-expansion-panel-title>
        <div class="error-panel__title">
          <v-icon color="warning" icon="mdi-alert-circle" size="20" />
          <div>
            <strong>{{ error.source }}</strong>
            <span>{{ error.message }}</span>
          </div>
        </div>
      </v-expansion-panel-title>
      <v-expansion-panel-text>
        <pre>{{ error.details }}</pre>
      </v-expansion-panel-text>
    </v-expansion-panel>
  </v-expansion-panels>
</template>

<style scoped src="./DashboardErrorPanels.css"></style>
