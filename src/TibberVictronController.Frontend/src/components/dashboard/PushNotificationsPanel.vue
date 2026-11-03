<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { disablePush, enablePush, pushState, testPush, type PushState } from '../../push';

const state = ref<PushState | null>(null);
const busy = ref(false);
const message = ref<{ type: 'success' | 'warning'; text: string } | null>(null);

async function refresh(): Promise<void> {
  state.value = await pushState().catch(() => 'unsupported' as const);
}

async function run(action: () => Promise<void>, success: string): Promise<void> {
  busy.value = true;
  try {
    await action();
    message.value = { type: 'success', text: success };
  } catch (reason) {
    message.value = { type: 'warning', text: reason instanceof Error ? reason.message : 'Aktion fehlgeschlagen.' };
  } finally {
    busy.value = false;
    await refresh();
  }
}

onMounted(() => void refresh());
</script>

<template>
  <v-card variant="outlined" aria-label="Benachrichtigungen">
    <div class="pa-4">
      <div class="d-flex align-center justify-space-between flex-wrap ga-2">
        <h2 class="text-h6">Benachrichtigungen auf diesem Gerät</h2>
        <v-chip v-if="state === 'on'" size="small" color="success">Eingeschaltet</v-chip>
      </div>
      <p class="push__meta mb-3">
        Fragt nach dem Anstecken unter dem Grund-Ladestand, ob das Auto sofort oder preisgesteuert laden soll.
        Tipp: Im Chrome-Menü „App installieren“ wählen, dann läuft EnergyFlowPilot wie eine App.
      </p>
      <v-alert v-if="state === 'unsupported'" type="info" variant="tonal" density="compact">
        Dieser Browser kann keine Benachrichtigungen empfangen (auf Android: Chrome verwenden, Seite über https öffnen).
      </v-alert>
      <v-alert v-else-if="state === 'blocked'" type="warning" variant="tonal" density="compact">
        Benachrichtigungen sind für diese Seite blockiert. In den Website-Einstellungen des Browsers erlauben.
      </v-alert>
      <div v-else-if="state" class="d-flex flex-wrap ga-2">
        <v-btn v-if="state === 'off'" color="primary" :loading="busy" @click="run(enablePush, 'Benachrichtigungen eingeschaltet.')">Einschalten</v-btn>
        <template v-else>
          <v-btn variant="tonal" :loading="busy" @click="run(testPush, 'Testnachricht gesendet.')">Testnachricht</v-btn>
          <v-btn variant="text" :disabled="busy" @click="run(disablePush, 'Benachrichtigungen ausgeschaltet.')">Ausschalten</v-btn>
        </template>
      </div>
      <v-alert v-if="message" class="mt-3" :type="message.type" variant="tonal" density="compact">{{ message.text }}</v-alert>
    </div>
  </v-card>
</template>

<style scoped>
.push__meta {
  font-size: 0.85rem;
  opacity: 0.75;
}
</style>
