import { createRouter, createWebHistory } from 'vue-router';
import { isAdmin } from './auth';
import DashboardPage from './pages/DashboardPage.vue';
import HeatingPage from './pages/HeatingPage.vue';
import EvChargingPage from './pages/EvChargingPage.vue';
import SettingsPage from './pages/SettingsPage.vue';
import AnalysisPage from './pages/AnalysisPage.vue';
import ControlPage from './pages/ControlPage.vue';
import AutomationsPage from './pages/AutomationsPage.vue';

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/',
      redirect: '/dashboard'
    },
    {
      path: '/dashboard',
      name: 'dashboard',
      component: DashboardPage
    },
    {
      path: '/analysis',
      name: 'analysis',
      component: AnalysisPage
    },
    {
      path: '/control',
      name: 'control',
      component: ControlPage
    },
    {
      path: '/automations',
      name: 'automations',
      component: AutomationsPage
    },
    {
      path: '/heating',
      name: 'heating',
      component: HeatingPage
    },
    {
      path: '/car',
      name: 'car',
      component: EvChargingPage
    },
    {
      path: '/settings',
      name: 'settings',
      component: SettingsPage,
      meta: { requiresAdmin: true }
    }
  ]
});

// Viewers have no use for the configuration page; the backend would reject every change anyway.
router.beforeEach((to) => (to.meta.requiresAdmin && !isAdmin.value ? { name: 'dashboard' } : true));
