<script setup lang="ts">
import { computed } from 'vue';
import { errorText, missingSettingsOf } from '../zentrale/zentraleApi';
import SettingQuickFix from './SettingQuickFix.vue';

/** An error from the API; when it names missing settings, they can be entered right here (see SettingQuickFix). */
const props = defineProps<{ error: unknown; fallback?: string }>();
const emit = defineEmits<{ retry: [] }>();
const text = computed(() => (typeof props.error === 'string' ? props.error : errorText(props.error, props.fallback ?? 'Fehler beim Laden.')));
const keys = computed(() => missingSettingsOf(props.error));
</script>

<template>
  <div v-if="error" role="alert">
    <p class="z-note" :class="keys.length ? 'z-note--warning' : 'z-note--error'">{{ text }}</p>
    <SettingQuickFix v-if="keys.length" :keys="keys" @saved="emit('retry')" />
  </div>
</template>
