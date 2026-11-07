<script setup lang="ts">
import { governanceLabel, governanceSourceLabel, type PluginGovernance, type PluginGovernanceSource } from '../../api/plugin-governance'
defineProps<{ state?: PluginGovernance; source?: PluginGovernanceSource; installed?: boolean; sourceOnly?: boolean; showSource?: boolean }>()
</script>
<template>
  <section class="plugin-governance" :class="{ 'plugin-governance--revoked': state?.status === 'revoked' }" aria-label="包治理状态">
    <template v-if="!sourceOnly">
      <p><strong>{{ installed ? '当前已安装包：' : '目标包：' }}{{ governanceLabel(state) }}</strong></p>
      <p v-if="state?.reason">原因：{{ state.reason }}</p>
      <p v-if="state?.updatedAt">状态记录时间：{{ state.updatedAt }}</p>
      <p v-if="installed && state?.status === 'revoked'">宿主不会静默停用或删除已安装包。请检查影响，并主动停用、卸载或迁移。</p>
    </template>
    <p v-if="source || sourceOnly || showSource">{{ governanceSourceLabel(source) }}</p>
    <p v-if="source?.generatedAt">源生成时间：{{ source.generatedAt }}</p>
    <p v-if="source?.checkedAt">最近检查时间：{{ source.checkedAt }}</p>
  </section>
</template>
<style scoped>
.plugin-governance { border-left: 3px solid currentColor; padding: .35rem .7rem; margin-block: .5rem; font-size: .85rem; }
.plugin-governance p { margin-block: .25rem; overflow-wrap: anywhere; }
.plugin-governance--revoked { color: var(--color-danger, #a12626); }
</style>
