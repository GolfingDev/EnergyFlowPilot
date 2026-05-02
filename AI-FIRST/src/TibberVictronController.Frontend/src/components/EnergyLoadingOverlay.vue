<template>
  <v-overlay
    :model-value="modelValue"
    class="energy-loading-overlay"
    persistent
    scrim="rgba(4, 12, 32, 0.58)"
    z-index="9999"
  >
    <div class="energy-loader-wrapper">
      <div class="energy-loader-card">
        <img
          class="energy-loader-image"
          :style="{ width: `${size}px`, height: `${size}px` }"
          :src="imageSrc"
          alt="Loading EnergyFlowPilot data"
        />

        <div class="energy-loader-title">
          {{ title }}
        </div>

        <div class="energy-loader-subtitle">
          {{ subtitle }}
        </div>

        <v-progress-linear
          v-if="showProgress"
          indeterminate
          rounded
          height="4"
          class="energy-loader-progress"
        />
      </div>
    </div>
  </v-overlay>
</template>

<script setup lang="ts">
withDefaults(
  defineProps<{
    modelValue: boolean
    title?: string
    subtitle?: string
    imageSrc?: string
    showProgress?: boolean
    size?: number
  }>(),
  {
    title: 'Lade Daten...',
    subtitle: 'Optimiere den Energy Flow…',
    imageSrc: 'loading.gif',
    showProgress: true,
    size: 200
  }
)
</script>

<style scoped>
.energy-loading-overlay :deep(.v-overlay__scrim) {
  background:
    radial-gradient(circle at 50% 35%, rgba(0, 210, 255, 0.14), transparent 38%),
    rgba(4, 12, 32, 0.58) !important;
  backdrop-filter: blur(8px);
  -webkit-backdrop-filter: blur(8px);
}

.energy-loading-overlay :deep(.v-overlay__content) {
  inset: 0 !important;
  width: 100vw;
  height: 100vh;
  max-width: none;
  display: flex;
  align-items: center;
  justify-content: center;
}

.energy-loader-wrapper {
  width: 100%;
  height: 100%;
  display: grid;
  place-items: center;
  padding: 16px;
}

.energy-loader-card {
  width: min(260px, calc(100vw - 32px));
  padding: 16px 16px 14px;
  border-radius: 22px;
  background:
    radial-gradient(circle at 50% 25%, rgba(0, 210, 255, 0.16), transparent 42%),
    linear-gradient(180deg, rgba(10, 24, 52, 0.92), rgba(4, 12, 32, 0.96));
  border: 1px solid rgba(104, 232, 255, 0.22);
  box-shadow:
    0 18px 54px rgba(0, 0, 0, 0.38),
    0 0 32px rgba(0, 210, 255, 0.1);
  text-align: center;
}

.energy-loader-image {
  object-fit: contain;
  display: block;
  margin: 0 auto 8px;
  border-radius: 14px;
}

.energy-loader-title {
  color: #eef8ff;
  font-size: 0.95rem;
  font-weight: 700;
  line-height: 1.25;
}

.energy-loader-subtitle {
  margin-top: 4px;
  color: rgba(206, 236, 245, 0.72);
  font-size: 0.78rem;
  line-height: 1.35;
}

.energy-loader-progress {
  margin-top: 12px;
  color: #39f59a;
}
</style>