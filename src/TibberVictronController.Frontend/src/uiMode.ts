import { ref, watch } from 'vue';

const storageKey = 'efp-expert-mode';

function readStoredExpertMode(): boolean {
  try {
    return localStorage.getItem(storageKey) === 'true';
  } catch {
    return false;
  }
}

/**
 * Two levels of the same interface: the standard level hides technical parameters, the expert level shows them.
 * It never hides safety-relevant states; those are visible on both levels.
 */
export const expertMode = ref(readStoredExpertMode());

watch(expertMode, (value) => {
  try {
    localStorage.setItem(storageKey, String(value));
  } catch {
    // Without storage the choice simply lasts for this visit.
  }
});
