import { createRouter, createWebHistory } from 'vue-router';
import DashboardPage from './pages/DashboardPage.vue';
import HeatingPage from './pages/HeatingPage.vue';
import SettingsPage from './pages/SettingsPage.vue';

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
      path: '/heating',
      name: 'heating',
      component: HeatingPage
    },
    {
      path: '/settings',
      name: 'settings',
      component: SettingsPage
    }
  ]
});
