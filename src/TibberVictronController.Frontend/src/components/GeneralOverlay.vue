<template>
  <v-overlay
    :model-value="modelValue"
    class="image-overlay"
    scrim="transparent"
    z-index="9999"
    @click="close"
  >
    <div class="image-overlay-backdrop"></div>

    <div class="image-overlay-content">
      <img
        class="image-overlay-img"
        :src="imageSrc"
        :alt="alt"
        :style="{
          width: computedWidth,
          maxWidth: maxWidth,
          maxHeight: maxHeight
        }"
      />
    </div>
  </v-overlay>
</template>

<script setup lang="ts">
import { computed } from 'vue'

const props = withDefaults(
  defineProps<{
    modelValue: boolean
    imageSrc: string
    alt?: string
    width?: number | string
    maxWidth?: string
    maxHeight?: string
  }>(),
  {
    alt: 'Overlay image',
    width: '90vw',
    maxWidth: '90vw',
    maxHeight: '90vh'
  }
)

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
}>()

const computedWidth = computed(() => {
  return typeof props.width === 'number'
    ? `${props.width}px`
    : props.width
})

function close() {
  emit('update:modelValue', false)
}
</script>

<style scoped>
.image-overlay :deep(.v-overlay__content) {
  inset: 0 !important;
  width: 100vw;
  height: 100vh;
  max-width: none;
  display: flex;
  align-items: center;
  justify-content: center;
}

.image-overlay-backdrop {
  position: fixed;
  inset: 0;
  background: rgba(4, 12, 32, 0.56);
  backdrop-filter: blur(8px);
  -webkit-backdrop-filter: blur(8px);
}

.image-overlay-content {
  position: relative;
  z-index: 1;
  width: 100%;
  height: 100%;
  display: grid;
  place-items: center;
  padding: 16px;
  cursor: pointer;
}

.image-overlay-img {
  display: block;
  height: auto;
  object-fit: contain;
  border-radius: 18px;
  filter:
    drop-shadow(0 18px 38px rgba(0, 0, 0, 0.42))
    drop-shadow(0 0 22px rgba(0, 210, 255, 0.16));
  cursor: pointer;
}
</style>