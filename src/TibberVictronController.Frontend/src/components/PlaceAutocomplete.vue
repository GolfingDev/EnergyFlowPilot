<script setup lang="ts">
import { ref, watch } from 'vue';

export interface PlaceSuggestion {
  label: string;
  latitude: number;
  longitude: number;
}

// Free text stays possible; a chosen suggestion additionally provides its coordinates.
const props = defineProps<{ label: string; modelValue: string }>();
const emit = defineEmits<{ 'update:modelValue': [value: string]; select: [place: PlaceSuggestion | null] }>();

const search = ref(props.modelValue);
const items = ref<PlaceSuggestion[]>([]);
const loading = ref(false);
const selected = ref<PlaceSuggestion | null>(null);
let timer: number | undefined;
let requestNumber = 0;

watch(() => props.modelValue, value => {
  if (value !== search.value) search.value = value;
  if (!value) selected.value = null;
});

watch(search, value => {
  const text = value ?? '';
  emit('update:modelValue', text);
  if (selected.value && text !== selected.value.label) {
    selected.value = null;
    emit('select', null);
  }
  window.clearTimeout(timer);
  if (text.trim().length < 3 || selected.value) {
    items.value = selected.value ? [selected.value] : [];
    return;
  }
  // Wait for a typing pause: the public address service asks for fair use.
  timer = window.setTimeout(() => void suggest(text.trim()), 350);
});

async function suggest(query: string): Promise<void> {
  const number = ++requestNumber;
  loading.value = true;
  try {
    const response = await fetch(`/api/places?q=${encodeURIComponent(query)}`);
    if (response.ok && number === requestNumber) items.value = await response.json() as PlaceSuggestion[];
  } catch {
    // Suggestions are optional; free text still works.
  } finally {
    if (number === requestNumber) loading.value = false;
  }
}

function choose(value: PlaceSuggestion | string | null): void {
  if (value && typeof value === 'object') {
    selected.value = value;
    search.value = value.label;
    emit('update:modelValue', value.label);
    emit('select', value);
  }
}
</script>

<template>
  <v-combobox
    :model-value="selected ?? search"
    v-model:search="search"
    :items="items"
    item-title="label"
    return-object
    no-filter
    hide-no-data
    :loading="loading"
    :label="label"
    density="compact"
    hide-details
    @update:model-value="choose"
  />
</template>
